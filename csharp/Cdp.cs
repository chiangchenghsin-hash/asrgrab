using System.Diagnostics;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace AsrGrab;

public sealed class VideoState
{
    public bool NoVideo { get; init; }
    public bool Paused { get; init; }
    public bool Ended { get; init; }
    public double CurrentTime { get; init; }
    public double Duration { get; init; }
    public bool Muted { get; init; }
    public string Src { get; init; } = "";
    public bool IsLive { get; init; }
}

/// CDP WebSocket 客户端（System.Net.WebSockets，无第三方依赖）。语义对齐 csharp/tools/cdp_video.py。
public sealed class CdpClient : IDisposable
{
    private const string ProbeJs = """
        JSON.stringify((() => {
          const vs = Array.from(document.querySelectorAll('video'));
          const v = vs.find(x => x.duration > 0) || vs[0];
          if (!v) return { no_video: true };
          return {
            no_video: false,
            paused: v.paused,
            ended: v.ended,
            ct: v.currentTime,
            dur: isFinite(v.duration) ? v.duration : 0,
            live: v.duration === Infinity,
            muted: v.muted,
            src: (v.currentSrc || '').slice(0, 200),
          };
        })())
        """;

    private const string PlayJs = """
        JSON.stringify((() => {
          const vs = Array.from(document.querySelectorAll('video'));
          const v = vs.find(x => x.duration > 0) || vs[0];
          if (!v) return { no_video: true };
          v.muted = false;
          const p = v.play();
          return { called: true, err: p && p.catch ? 'async' : 'sync' };
        })())
        """;

    private const string PauseSeek0Js = """
        JSON.stringify((() => {
          const vs = Array.from(document.querySelectorAll('video'));
          const v = vs.find(x => x.duration > 0) || vs[0];
          if (!v) return { no_video: true };
          v.pause();
          v.currentTime = 0;
          v.muted = false;
          return { called: true };
        })())
        """;

    private readonly ClientWebSocket _ws = new();
    private int _id;

    private CdpClient() { }

    public static CdpClient Attach(int port, int waitSec = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(waitSec);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                var list = http.GetStringAsync($"http://127.0.0.1:{port}/json/list").GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(list);
                string? wsUrl = null;
                foreach (var t in doc.RootElement.EnumerateArray())
                {
                    if (t.GetProperty("type").GetString() != "page") continue;
                    wsUrl = t.GetProperty("webSocketDebuggerUrl").GetString();
                    break;
                }
                if (wsUrl == null) { Thread.Sleep(400); continue; }
                var c = new CdpClient();
                c._ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None).GetAwaiter().GetResult();
                return c;
            }
            catch (Exception e) { last = e; Thread.Sleep(400); }
        }
        throw new CliError(ExitCodes.Dependency, "DEPENDENCY", $"无法连接 CDP :{port}",
            new() { ["detail"] = last?.Message });
    }

    /// 新开标签页打开 url 并连接它（spec §3.2）；/json/new 不可用时退回首页面 + Navigate（告警由调用方决定）。
    public static CdpClient OpenPage(int port, string url, int waitSec = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(waitSec);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                // 新版 Edge/Chrome 用 PUT /json/new，旧版 GET；逐个试
                string? created = null;
                foreach (var method in new[] { "PUT", "GET" })
                {
                    try
                    {
                        using var req = new HttpRequestMessage(new HttpMethod(method), $"http://127.0.0.1:{port}/json/new?{Uri.EscapeDataString(url)}");
                        using var resp = http.SendAsync(req).GetAwaiter().GetResult();
                        if (resp.IsSuccessStatusCode) { created = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult(); break; }
                    }
                    catch { }
                }
                if (created != null)
                {
                    using var doc = JsonDocument.Parse(created);
                    var wsUrl = doc.RootElement.TryGetProperty("webSocketDebuggerUrl", out var u) ? u.GetString() : null;
                    if (!string.IsNullOrEmpty(wsUrl))
                    {
                        var c = new CdpClient();
                        c._ws.ConnectAsync(new Uri(wsUrl!), CancellationToken.None).GetAwaiter().GetResult();
                        return c;
                    }
                }
            }
            catch (Exception e) { last = e; }
            Thread.Sleep(400);
        }
        // 退回：附着首页面 + Navigate（旧路径；调用方应告警）
        Console.Error.WriteLine("警告：/json/new 不可用，退回附着首页面并 Navigate（可能影响已有页）");
        var fallback = Attach(port, waitSec);
        fallback.Navigate(url);
        return fallback;
    }

    private JsonElement Call(string method, object? parms)
    {
        var id = Interlocked.Increment(ref _id);
        var payload = JsonSerializer.Serialize(new { id, method, @params = parms });
        var bytes = Encoding.UTF8.GetBytes(payload);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            _ws.SendAsync(bytes, WebSocketMessageType.Text, true, cts.Token).GetAwaiter().GetResult();
            var buf = new byte[1 << 20];
            while (true)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = _ws.ReceiveAsync(buf, cts.Token).GetAwaiter().GetResult();
                    if (r.MessageType == WebSocketMessageType.Close)
                        throw new CliError(ExitCodes.Engine, "CDP_CLOSED", "CDP WebSocket 已关闭");
                    ms.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);
                using var doc = JsonDocument.Parse(ms.ToArray());
                if (!doc.RootElement.TryGetProperty("id", out var got) || got.GetInt32() != id) continue;
                if (doc.RootElement.TryGetProperty("error", out var err))
                    throw new CliError(ExitCodes.Engine, "CDP_ERROR", err.ToString());
                return doc.RootElement.GetProperty("result").Clone();
            }
        }
        catch (OperationCanceledException)
        {
            throw new CliError(ExitCodes.Engine, "CDP_TIMEOUT", "CDP 调用超时（30s）");
        }
    }

    public string EvalJs(string expression)
    {
        var result = Call("Runtime.evaluate", new
        {
            expression,
            returnByValue = true,
            awaitPromise = true,
        });
        if (result.TryGetProperty("exceptionDetails", out var ex))
            throw new CliError(ExitCodes.Engine, "EVAL_ERROR", ex.ToString());
        if (!result.TryGetProperty("result", out var inner)) return "";
        if (inner.TryGetProperty("subtype", out var st) && st.GetString() == "error")
            throw new CliError(ExitCodes.Engine, "EVAL_ERROR",
                inner.TryGetProperty("description", out var d) ? d.ToString() : "JS eval error");
        return inner.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";
    }

    public void Navigate(string url) => Call("Page.navigate", new { url });

    public VideoState Probe()
    {
        var raw = EvalJs(ProbeJs);
        if (string.IsNullOrEmpty(raw)) return new VideoState { NoVideo = true };
        using var d = JsonDocument.Parse(raw);
        var e = d.RootElement;
        if (e.TryGetProperty("no_video", out var nv) && nv.GetBoolean())
            return new VideoState { NoVideo = true };
        return new VideoState
        {
            Paused = e.GetProperty("paused").GetBoolean(),
            Ended = e.GetProperty("ended").GetBoolean(),
            CurrentTime = e.GetProperty("ct").GetDouble(),
            Duration = e.GetProperty("dur").GetDouble(),
            Muted = e.GetProperty("muted").GetBoolean(),
            Src = e.GetProperty("src").GetString() ?? "",
            IsLive = e.GetProperty("live").GetBoolean(),
        };
    }

    public string Play() => EvalJs(PlayJs);

    /// play() 后验证进度确实在走（自动播放策略拒绝时返回 false）。
    public bool PlayAndVerify(double waitSec = 2.0)
    {
        var before = Probe();
        EvalJs(PlayJs);
        Thread.Sleep((int)(waitSec * 1000));
        var after = Probe();
        return !after.NoVideo && (!after.Paused || after.CurrentTime > before.CurrentTime + 0.05);
    }

    public void PauseSeek0Unmute() => EvalJs(PauseSeek0Js);

    private static bool Transient(Exception e)
        => e is JsonException
           || (e is CliError ce && ce.Code is "EVAL_ERROR" or "CDP_ERROR" or "CDP_TIMEOUT");

    /// 等待顶层 video 就绪（duration>0）。直播（duration 非有限）直接返回由调用方拒绝。
    /// 超时抛 CliError(SOURCE)，附首登提示（spec §6）。
    public VideoState WaitForVideo(int timeoutSec, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
        while (DateTime.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested)
                throw new CliError(ExitCodes.Internal, "CANCELLED", "grab_url 已取消");
            VideoState s;
            try
            {
                s = Probe();
            }
            catch (Exception e) when (Transient(e))
            {
                // 页面跳转/上下文重建等瞬态错误：继续轮询到截止
                Thread.Sleep(500);
                continue;
            }
            if (s.IsLive) return s;               // 调用方判 live → 拒绝
            if (!s.NoVideo && s.Duration > 0) return s;
            Thread.Sleep(500);
        }
        throw new CliError(ExitCodes.Source, "SOURCE", $"等待视频超时（{timeoutSec}s）", new()
        {
            ["hint"] = "可能需要登录：asrgrab grab_url <url> --keep-browser 打开页面手动登录后重跑；或加大 --video-timeout",
        });
    }

    /// 轮询结束条件（1s 一次）；返回触发的 EndReason。
    /// 瞬态 CDP 错误重试（连续 5 次放弃）；终态失败抛 CliError（调用方 catch 后落 grab_end，保留 error 文本与退出码）；ct 取消抛 CANCELLED。
    public EndReason WaitForEnd(string? firstSrc, int maxSeconds, Stopwatch sw, CancellationToken ct)
    {
        var transientStreak = 0;
        while (true)
        {
            if (ct.IsCancellationRequested)
                throw new CliError(ExitCodes.Internal, "CANCELLED", "grab_url 已取消");
            EndCondition.VideoSample sample;
            try
            {
                var s = Probe();
                sample = new EndCondition.VideoSample(s.CurrentTime, s.Duration, s.Ended, s.Src);
            }
            catch (Exception e) when (Transient(e))
            {
                if (++transientStreak >= 5)
                    throw new CliError(ExitCodes.Engine, "CDP_ERROR", "连续 5 次瞬态 CDP 失败，放弃等待");
                Thread.Sleep(1000);
                continue;
            }
            catch (CliError)
            {
                throw;
            }
            catch (Exception e)
            {
                throw new CliError(ExitCodes.Engine, "CDP_ERROR", e.Message);
            }
            transientStreak = 0;
            var (stop, reason) = EndCondition.Evaluate(sample, firstSrc, sw.Elapsed.TotalSeconds, maxSeconds);
            if (stop) return reason!.Value;
            Thread.Sleep(1000);
        }
    }

    public void Dispose()
    {
        try { _ws.Abort(); _ws.Dispose(); } catch { }
    }
}
