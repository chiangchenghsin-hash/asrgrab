using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace AsrGrab;

/// Edge 进程生命周期：端口探测 / 拉起（launch_edge.bat 同款参数）/ 关闭。
public sealed class EdgeHost : IDisposable
{
    private readonly Process _proc;
    private readonly int _port;
    private EdgeHost(Process proc, int port)
    {
        _proc = proc;
        _port = port;
    }

    public static bool PortOpen(int port)
    {
        try
        {
            using var c = new TcpClient();
            c.Connect("127.0.0.1", port);
            return true;
        }
        catch { return false; }
    }

    public static EdgeHost Launch(int port)
    {
        var profile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".asrgrab", "edge-profile");
        var sawExe = false;
        foreach (var exe in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
        })
        {
            if (!File.Exists(exe)) continue;
            sawExe = true;
            var psi = new ProcessStartInfo { FileName = exe, UseShellExecute = false };
            psi.ArgumentList.Add($"--remote-debugging-port={port}");
            psi.ArgumentList.Add("--remote-allow-origins=*");
            // grab_url 无人值守抓取：页面必须能无手势开播；专用 profile 下风险可控
            psi.ArgumentList.Add("--autoplay-policy=no-user-gesture-required");
            psi.ArgumentList.Add($"--user-data-dir={profile}");
            psi.ArgumentList.Add("--no-first-run");
            psi.ArgumentList.Add("--no-default-browser-check");
            psi.ArgumentList.Add("--proxy-bypass-list=127.0.0.1;localhost");
            psi.ArgumentList.Add("--disable-features=msEdgeStartupBoost");
            psi.ArgumentList.Add("about:blank");
            Process? p = null;
            try
            {
                p = Process.Start(psi);
            }
            catch
            {
                // 启动失败（AV 拦截 / 损坏安装 / 权限），试下一个路径
                continue;
            }
            if (p == null) continue;
            var host = new EdgeHost(p, port);
            // 等 CDP 端口就绪（最多 5s）
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && !PortOpen(port))
                Thread.Sleep(250);
            if (!PortOpen(port))
            {
                var exited = p.HasExited;
                try { host.Close(); } catch { }
                throw new CliError(ExitCodes.Dependency, "DEPENDENCY", "Edge 启动后 CDP 端口未就绪", new()
                {
                    ["detail"] = exited ? "进程已立即退出（profile/端口可能被占用）" : "进程仍在但端口未监听",
                    ["hint"] = "可能有 --keep-browser 会话或旧实例占用 ~/.asrgrab/edge-profile；关闭后重试",
                });
            }
            return host;
        }
        throw new CliError(ExitCodes.Dependency, "DEPENDENCY",
            sawExe ? "无法启动 Edge" : "未找到 msedge.exe");
    }

    public void Close()
    {
        // Chromium 多进程：Process.Start 拿到的是 launcher，真 browser 被重收养，
        // CloseMainWindow/Kill(entireProcessTree) 会打空。改三段式尽力关闭。
        try { CloseViaCdp(); } catch { }
        if (PortOpen(_port))
        {
            try { KillProfileProcesses(); } catch { }
            var deadline = DateTime.UtcNow.AddSeconds(2.5);
            while (DateTime.UtcNow < deadline && PortOpen(_port))
                Thread.Sleep(200);
        }
        try
        {
            if (!_proc.HasExited)
            {
                _proc.CloseMainWindow();
                if (!_proc.WaitForExit(1500)) _proc.Kill(entireProcessTree: true);
            }
        }
        catch { /* 最后兜底，尽力而为 */ }
    }

    /// CDP Browser.close 是唯一可靠的整树关闭（不读回执，服务器会断连）。
    private void CloseViaCdp()
    {
        string? wsUrl = null;
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) })
        {
            var json = http.GetStringAsync($"http://127.0.0.1:{_port}/json/version").GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            wsUrl = doc.RootElement.GetProperty("webSocketDebuggerUrl").GetString();
        }
        if (string.IsNullOrEmpty(wsUrl)) return;

        using var ws = new ClientWebSocket();
        ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None).GetAwaiter().GetResult();
        var payload = Encoding.UTF8.GetBytes("""{"id":1,"method":"Browser.close"}""");
        ws.SendAsync(payload, WebSocketMessageType.Text, true, CancellationToken.None).GetAwaiter().GetResult();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && PortOpen(_port))
            Thread.Sleep(200);
    }

    /// 仅清理本 profile（~/.asrgrab/edge-profile）的 msedge，不动用户日常 Edge。
    private static void KillProfileProcesses()
    {
        var psi = new ProcessStartInfo { FileName = "powershell.exe", UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add("Get-CimInstance Win32_Process -Filter \"Name='msedge.exe'\" | Where-Object { $_.CommandLine -match 'asrgrab\\\\edge-profile' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }");
        using var pp = Process.Start(psi);
        pp?.WaitForExit(10_000);
    }

    public void Dispose()
    {
        Close();
        try { _proc.Dispose(); } catch { }
    }
}
