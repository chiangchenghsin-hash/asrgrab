using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using NAudio.CoreAudioApi;

namespace AsrGrab;

public static class Commands
{
    private static readonly HashSet<string> MediaExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".wav", ".mp3", ".m4a", ".flac", ".aac", ".ogg", ".wma", ".opus",
        ".mp4", ".mkv", ".mov", ".webm", ".avi", ".ts", ".flv", ".wmv", ".m4v",
    };

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------
    private static string DefaultModelsDir(string? overrideDir)
    {
        if (!string.IsNullOrWhiteSpace(overrideDir)) return Path.GetFullPath(overrideDir);
        var env = Environment.GetEnvironmentVariable("ASRGRAB_MODELS");
        if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);
        return Path.Combine(Environment.CurrentDirectory, "models");
    }

    private static string NormalizeModel(string model) => SherpaEngine.NormalizeKind(model);

    private static double D(Dictionary<string, object?> d, string key)
    {
        if (!d.TryGetValue(key, out var v) || v is null) return 0;
        if (v is JsonElement je) return je.ValueKind == JsonValueKind.Number ? je.GetDouble() : 0;
        return Convert.ToDouble(v);
    }

    private static long L(Dictionary<string, object?> d, string key)
    {
        if (!d.TryGetValue(key, out var v) || v is null) return 0;
        if (v is JsonElement je) return je.ValueKind == JsonValueKind.Number ? je.GetInt64() : 0;
        return Convert.ToInt64(v);
    }

    private static int I(Dictionary<string, object?> d, string key) => (int)L(d, key);

    private static bool B(Dictionary<string, object?> d, string key)
    {
        if (!d.TryGetValue(key, out var v) || v is null) return false;
        if (v is bool b) return b;
        if (v is JsonElement je) return je.ValueKind == JsonValueKind.True;
        return false;
    }

    private static string S(Dictionary<string, object?> d, string key)
        => SessionStore.GetStr(d, key) ?? "";

    // ---------------------------------------------------------------------
    // help / devices
    // ---------------------------------------------------------------------
    public static int Help()
    {
        Console.Error.WriteLine("""
asrgrab 2.0 — 本地逐字转写 CLI (.NET 8 / Windows 11)

用法:
  asrgrab <command> [options]

命令:
  devices     列出音源（OBS 式：系统声音 / 输入设备）
  start       开始抓取并实时转写（或 --record-only 只录音）
  stop        停止当前会话
  status      查看会话状态
  transcribe  离线转写文件/目录（视频/音频）
  models      查看/下载离线模型
  demo        生成示例转写（无需模型，方便评审看输出）
  doctor      环境自检
  schema      输出机器可读契约（Agent 先读这个）

示例:
  asrgrab devices --human
  asrgrab start --source 0 --chunk 8 --human
  asrgrab start --source 0 --detach
  asrgrab status --tail 20
  asrgrab stop
  asrgrab transcribe .\video.mp4 --out .\transcripts
  asrgrab models --download
""");
        return ExitCodes.Ok;
    }

    public static int Devices(string[] args)
    {
        var ap = new ArgParser(args);
        var kind = ap.Str("kind", "all");
        var list = AudioSourceEnumerator.List();
        if (kind != "all") list = list.Where(s => s.Kind == kind).ToList();

        if (Out.Human) PrintSourceTable(list);
        else Out.Json(new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["count"] = list.Count,
            ["sources"] = list.Select(s => s.Public()).ToList(),
        });

        return ExitCodes.Ok;
    }

    private static void PrintSourceTable(List<AudioSource> list)
    {
        if (list.Count == 0)
        {
            Console.Error.WriteLine("未找到任何音源。检查 Windows 声音设置，或确认有可用的输出/输入设备。");
            return;
        }

        Console.Error.WriteLine($"音源列表（共 {list.Count}）");
        Console.Error.WriteLine();
        Console.Error.WriteLine($"  {"idx",-5}{"类型",-10}{"默认",-6}{"声道",-6}名称");
        Console.Error.WriteLine("  " + new string('─', 72));

        foreach (var s in list)
        {
            var kind = s.Kind == "loopback" ? "系统声音" : "输入设备";
            var mark = s.IsDefault ? "." : " ";
            if (s.IsDefault) Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Error.WriteLine($"  [{s.Index,2}] {kind,-10}{mark,-6}{s.Channels,-6}{s.Name}");
            Console.ResetColor();
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine("用法: asrgrab start --source <编号|id|名称>");
    }

    // ---------------------------------------------------------------------
    // start / stop / status
    // ---------------------------------------------------------------------
    public static int Start(string[] args)
    {
        var ap = new ArgParser(args);
        var human = Out.Human;

        var cur = SessionStore.ReadCurrent();
        if (cur != null)
        {
            var st = SessionStore.GetStatus(cur.SessionId, 0);
            var curState = S(st, "state");
            if ((curState is "running" or "loading_model" or "starting") && SessionStore.PidAlive(cur.Pid))
            {
                throw new CliError(ExitCodes.Busy, "BUSY", "已有会话在运行", new()
                {
                    ["session_id"] = cur.SessionId,
                    ["hint"] = "先执行: asrgrab stop",
                });
            }
        }

        var sources = AudioSourceEnumerator.List();
        if (sources.Count == 0)
            throw new CliError(ExitCodes.Source, "SOURCE", "未找到任何可用音源（检查 Windows 声音设备）");

        AudioSource source;
        var spec = ap.Get("source");
        if (!string.IsNullOrWhiteSpace(spec))
        {
            source = SourceResolver.Resolve(spec, sources);
        }
        else if (ap.Flag("non-interactive") || Console.IsInputRedirected)
        {
            source = SourceResolver.Default(sources);
        }
        else
        {
            source = SourcePicker.Pick(sources);
        }

        var cfg = new SessionConfig
        {
            SessionId = SessionStore.NowId(),
            Source = source,
            OutDir = ap.Str("out", "transcripts"),
            ChunkSec = Math.Max(1, ap.Int("chunk", 12)),
            Model = NormalizeModel(ap.Str("model", "small")),
            ModelsDir = DefaultModelsDir(ap.Get("models-dir")),
            NoDiarize = ap.Flag("no-diarize"),
            RecordOnly = ap.Flag("record-only"),
            WavPath = ap.Get("wav"),
            MaxSeconds = Math.Max(0, ap.Int("max-seconds", 0)),
            NumSpeakers = Math.Max(0, ap.Int("num-speakers", 0)),
        };

        if (ap.Flag("detach")) return SpawnDetached(cfg);

        var state = SessionRunner.Run(cfg, MakeConsole(human));
        if (human)
        {
            var outFile = S(state, "out_file");
            var wavFile = S(state, "wav_file");
            Console.Error.WriteLine();
            if (!string.IsNullOrEmpty(outFile)) Console.Error.WriteLine($"转写文件：{outFile}");
            if (!string.IsNullOrEmpty(wavFile)) Console.Error.WriteLine($"录音文件：{wavFile}");
        }
        else
        {
            Out.Json(new Dictionary<string, object?>
            {
                ["event"] = "done",
                ["ok"] = true,
                ["result"] = state,
            });
        }

        return ExitCodes.Ok;
    }

    private static Action<Dictionary<string, object?>> MakeConsole(bool human)
    {
        if (!human) return ev => Out.Json(ev);

        return ev =>
        {
            switch (S(ev, "event"))
            {
                case "session_start":
                    LiveConsole.ClearLine();
                    Console.Error.WriteLine($"▶ 开始：{S(ev, "source_name")}  →  {S(ev, "out_file")}{S(ev, "wav_file")}");
                    break;
                case "model_loading":
                    LiveConsole.ClearLine();
                    Console.Error.WriteLine("⏳ 加载模型（首次使用若本地缺失会自动准备，仅需联网一次）…");
                    break;
                case "model_ready":
                    LiveConsole.ClearLine();
                    Console.Error.WriteLine("✓ 模型就绪，开始转写");
                    break;
                case "segment":
                    LiveConsole.ClearLine();
                    PrintSegment(ev);
                    break;
                case "ui_tick":
                    LiveConsole.Tick(D(ev, "elapsed_s"), D(ev, "level_db"), L(ev, "bytes"), I(ev, "segments"));
                    break;
                case "error":
                    LiveConsole.ClearLine();
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Error.WriteLine($"✗ {S(ev, "code")}: {S(ev, "message")}");
                    Console.ResetColor();
                    break;
                case "session_end":
                    LiveConsole.ClearLine();
                    Console.Error.WriteLine($"■ 结束  {S(ev, "elapsed_s")}s  {S(ev, "segments")} 句");
                    break;
            }
        };
    }

    private static void PrintSegment(Dictionary<string, object?> ev)
    {
        var speaker = S(ev, "speaker");
        var text = S(ev, "text");
        var ts = $"[{Out.FmtMs(I(ev, "start_ms"))} - {Out.FmtMs(I(ev, "end_ms"))}]";
        if (!string.IsNullOrEmpty(speaker))
        {
            Console.Error.Write($"{ts} ");
            Console.ForegroundColor = SpeakerColor(speaker);
            Console.Error.Write($"{speaker}：");
            Console.ResetColor();
            Console.Error.WriteLine(text);
        }
        else
        {
            Console.Error.WriteLine($"{ts} {text}");
        }
    }

    private static ConsoleColor SpeakerColor(string speaker)
    {
        var c = speaker.Length > 0 ? speaker[^1] : 'A';
        return c switch
        {
            'A' => ConsoleColor.Cyan,
            'B' => ConsoleColor.Magenta,
            'C' => ConsoleColor.Yellow,
            'D' => ConsoleColor.Green,
            _ => ConsoleColor.Gray,
        };
    }

    private static int SpawnDetached(SessionConfig cfg)
    {
        var sdir = SessionStore.SessionDir(cfg.SessionId);
        Directory.CreateDirectory(sdir);
        var cfgPath = Path.Combine(sdir, "config.json");
        JsonIo.Write(cfgPath, cfg, pretty: true);

        var exe = Environment.ProcessPath ?? "dotnet";
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.CurrentDirectory,
        };

        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        }

        psi.ArgumentList.Add("_run");
        psi.ArgumentList.Add("--config");
        psi.ArgumentList.Add(cfgPath);

        var proc = Process.Start(psi)
                   ?? throw new CliError(ExitCodes.Engine, "ENGINE", "无法启动后台进程");

        var statusPath = Path.Combine(sdir, "status.json");
        var deadline = DateTime.UtcNow.AddSeconds(20);
        Dictionary<string, object?>? st = null;

        while (DateTime.UtcNow < deadline)
        {
            st = JsonIo.Read<Dictionary<string, object?>>(statusPath);
            var state = st == null ? null : S(st, "state");
            if (state is "running" or "loading_model" or "starting")
            {
                var curStatus = st!;
                if (Out.Human)
                {
                    Console.Error.WriteLine($"▶ 后台运行中  session={S(curStatus, "session_id")}  pid={S(curStatus, "pid")}");
                    Console.Error.WriteLine($"  输出：{S(curStatus, "out_file")}{S(curStatus, "wav_file")}");
                    Console.Error.WriteLine("  状态：asrgrab status --human    停止：asrgrab stop --human");
                }
                else
                {
                    Out.Json(new Dictionary<string, object?>
                    {
                        ["ok"] = true,
                        ["session_id"] = S(curStatus, "session_id"),
                        ["pid"] = I(curStatus, "pid"),
                        ["state"] = state,
                        ["out_file"] = SessionStore.GetStr(curStatus, "out_file"),
                        ["wav_file"] = SessionStore.GetStr(curStatus, "wav_file"),
                        ["status_cmd"] = "asrgrab status --json",
                        ["stop_cmd"] = "asrgrab stop --json",
                    });
                }

                return ExitCodes.Ok;
            }

            if (proc.HasExited)
            {
                throw new CliError(ExitCodes.Engine, "ENGINE", $"后台进程启动失败（退出码 {proc.ExitCode}）", new()
                {
                    ["log"] = ReadTail(Path.Combine(sdir, "daemon.log")),
                });
            }

            Thread.Sleep(300);
        }

        throw new CliError(ExitCodes.Engine, "ENGINE", "后台进程启动超时", new()
        {
            ["log"] = ReadTail(Path.Combine(sdir, "daemon.log")),
            ["last_status"] = st,
        });
    }

    private static string ReadTail(string path, int limit = 2000)
    {
        try
        {
            if (!File.Exists(path)) return "";
            var text = File.ReadAllText(path, Encoding.UTF8);
            return text.Length <= limit ? text : text[^limit..];
        }
        catch
        {
            return "";
        }
    }

    public static int Stop(string[] args)
    {
        var ap = new ArgParser(args);
        var res = SessionStore.Stop(ap.Get("session-id"), 20);
        if (Out.Human)
        {
            Console.Error.WriteLine($"state={S(res, "state")}  {S(res, "message")}");
            var outFile = SessionStore.GetStr(res, "out_file");
            if (!string.IsNullOrEmpty(outFile)) Console.Error.WriteLine($"转写文件：{outFile}");
        }
        else
        {
            Out.Json(res);
        }

        return B(res, "ok") ? ExitCodes.Ok : ExitCodes.Busy;
    }

    public static int Status(string[] args)
    {
        var ap = new ArgParser(args);
        var res = SessionStore.GetStatus(ap.Get("session-id"), ap.Int("tail", 0));

        if (Out.Human)
        {
            if (S(res, "state") == "idle")
            {
                Console.Error.WriteLine(S(res, "message"));
            }
            else
            {
                Console.Error.WriteLine($"session : {S(res, "session_id")}");
                Console.Error.WriteLine($"state   : {S(res, "state")}");
                Console.Error.WriteLine($"source  : {S(res, "source")}");
                Console.Error.WriteLine($"elapsed : {S(res, "elapsed_s")}s");
                Console.Error.WriteLine($"level   : {S(res, "level_db")} dBFS");
                Console.Error.WriteLine($"segments: {S(res, "segments")}");
                Console.Error.WriteLine($"out     : {S(res, "out_file")}{S(res, "wav_file")}");
                if (res.TryGetValue("tail", out var t) && t is List<Segment> segs)
                    foreach (var s in segs) Console.Error.WriteLine(s.ToLine());
            }
        }
        else
        {
            Out.Json(res);
        }

        return ExitCodes.Ok;
    }

    // ---------------------------------------------------------------------
    // transcribe（离线文件/目录）
    // ---------------------------------------------------------------------
    public static int Transcribe(string[] args)
    {
        var ap = new ArgParser(args);
        if (ap.Positional.Count == 0)
            throw new CliError(ExitCodes.Usage, "USAGE", "缺少输入文件/目录。用法: asrgrab transcribe <文件|目录>");

        var input = ap.Positional[0];
        var files = CollectInputs(input);
        if (files.Count == 0)
            throw new CliError(ExitCodes.Usage, "USAGE", $"没有可转写的媒体文件: {input}");

        var human = Out.Human;
        var modelsDir = DefaultModelsDir(ap.Get("models-dir"));
        var model = NormalizeModel(ap.Str("model", "small"));
        var noDiarize = ap.Flag("no-diarize");
        var temp = Path.Combine(Path.GetTempPath(), "asrgrab_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        var results = new List<Dictionary<string, object?>>();

        try
        {
            SherpaEngine engine;
            try
            {
                engine = new SherpaEngine(new ModelPaths(modelsDir), model, !noDiarize, Math.Max(0, ap.Int("num-speakers", 0)), 2);
            }
            catch (CliError e)
            {
                if (human) Console.Error.WriteLine("✗ " + e.Message);
                else Out.Json(new Dictionary<string, object?> { ["ok"] = false, ["error"] = new { code = e.Code, message = e.Message } });
                return e.ExitCode;
            }

            using (engine)
            {
                foreach (var file in files)
                {
                    if (human) Console.Error.WriteLine($"▶ {file}");
                    else Out.Json(new { @event = "file_start", input = file });

                    try
                    {
                        var samples = AudioDecoder.DecodeTo16kMono(file, temp);
                        var segs = engine.Transcribe(samples, 16000, !noDiarize);
                        var md = ResolveMd(ap.Get("out"), file, files.Count > 1);

                        var meta = new Dictionary<string, object?>
                        {
                            ["created_at"] = SessionStore.NowIso(),
                            ["engine"] = $"sherpa-onnx {model}" + (noDiarize ? "" : " + diarization"),
                            ["source"] = new AudioSource { Kind = "file", Name = file },
                            ["input"] = file,
                            ["mode"] = "offline",
                        };

                        using (var w = new TranscriptWriter(md, meta))
                        {
                            w.Append(segs);
                        }

                        foreach (var s in segs)
                        {
                            if (human) Console.Error.WriteLine(s.ToLine());
                            else Out.Json(new { @event = "segment", start_ms = s.StartMs, end_ms = s.EndMs, speaker = s.Speaker, text = s.Text });
                        }

                        var item = new Dictionary<string, object?>
                        {
                            ["ok"] = true,
                            ["input"] = file,
                            ["out_file"] = md,
                            ["json_file"] = Path.ChangeExtension(md, ".json"),
                            ["segments"] = segs.Count,
                        };

                        if (human) Console.Error.WriteLine($"✓ → {md}  ({segs.Count} 句)");
                        else
                        {
                            var done = new Dictionary<string, object?> { ["event"] = "file_done" };
                            foreach (var kv in item) done[kv.Key] = kv.Value;
                            Out.Json(done);
                        }

                        results.Add(item);
                    }
                    catch (CliError e)
                    {
                        if (human) Console.Error.WriteLine($"✗ {file}: {e.Message}");
                        else Out.Json(new { @event = "error", input = file, code = e.Code, message = e.Message });
                        results.Add(new Dictionary<string, object?> { ["ok"] = false, ["input"] = file });
                    }
                }
            }
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }

        var succeeded = results.Count(r => B(r, "ok"));
        var okAll = succeeded == results.Count;
        if (human) Console.Error.WriteLine($"\n完成 {succeeded}/{results.Count}");
        else Out.Json(new { @event = "summary", ok = okAll, total = results.Count, succeeded, results });

        return okAll ? ExitCodes.Ok : ExitCodes.Engine;
    }

    private static List<string> CollectInputs(string path)
    {
        if (File.Exists(path)) return new List<string> { Path.GetFullPath(path) };
        if (Directory.Exists(path))
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Where(f => MediaExt.Contains(Path.GetExtension(f)))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        throw new CliError(ExitCodes.Usage, "USAGE", $"输入不存在: {path}");
    }

    private static string ResolveMd(string? outPath, string input, bool multi)
    {
        var stem = Path.GetFileNameWithoutExtension(input);
        if (string.IsNullOrWhiteSpace(outPath)) return Path.Combine("transcripts", stem + ".md");
        if (outPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            return multi ? Path.Combine(Path.GetDirectoryName(outPath) ?? ".", stem + ".md") : outPath;
        return Path.Combine(outPath, stem + ".md");
    }

    // ---------------------------------------------------------------------
    // doctor / schema / models / demo / daemon
    // ---------------------------------------------------------------------
    public static int Doctor(string[] args)
    {
        var ap = new ArgParser(args);
        var sources = AudioSourceEnumerator.List();
        var paths = new ModelPaths(DefaultModelsDir(ap.Get("models-dir")));

        var deps = new Dictionary<string, object?>
        {
            ["dotnet"] = Environment.Version.ToString(),
            ["naudio"] = typeof(MMDeviceEnumerator).Assembly.GetName().Version?.ToString() ?? "?",
            ["sherpa_native"] = File.Exists(Path.Combine(AppContext.BaseDirectory, "sherpa-onnx-c-api.dll")),
            ["ffmpeg"] = AudioDecoder.HasFfmpeg() ? "found" : "missing",
        };

        var notes = new List<string>();
        if (sources.Count == 0) notes.Add("未发现音源：检查 Windows 声音设备/隐私设置");
        if (!paths.HasAnyAsr) notes.Add("ASR 模型未就绪：asrgrab models --download");
        if (!paths.HasDiarization) notes.Add("说话人分离模型未就绪：asrgrab models --download");
        if (deps["ffmpeg"]?.ToString() == "missing") notes.Add("未找到 ffmpeg：transcribe 非 wav 视频/音频会失败");

        var info = new Dictionary<string, object?>
        {
            ["ok"] = sources.Count > 0,
            ["version"] = "2.0.0",
            ["platform"] = RuntimeInformation.OSDescription,
            ["arch"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["deps"] = deps,
            ["sources"] = new Dictionary<string, object?>
            {
                ["loopback"] = sources.Count(s => s.Kind == "loopback"),
                ["input"] = sources.Count(s => s.Kind == "input"),
            },
            ["default_source"] = sources.Count > 0 ? SourceResolver.Default(sources).Public() : null,
            ["models"] = paths.Status(),
            ["asr_ready"] = paths.HasAnyAsr && paths.HasDiarization,
            ["home"] = SessionStore.Root,
            ["notes"] = notes,
        };

        if (Out.Human)
        {
            Console.Error.WriteLine($"asrgrab 2.0  .NET {Environment.Version}  {RuntimeInformation.OSDescription}");
            foreach (var kv in deps) Console.Error.WriteLine($"  {kv.Key,-14}{kv.Value}");
            Console.Error.WriteLine($"  音源          loopback={sources.Count(s => s.Kind == "loopback")} input={sources.Count(s => s.Kind == "input")}");
            Console.Error.WriteLine($"  ASR 就绪      {info["asr_ready"]}");
            Console.Error.WriteLine($"  模型目录      {paths.Dir}");
            foreach (var n in notes) Console.Error.WriteLine($"! {n}");
        }
        else
        {
            Out.Json(info);
        }

        return sources.Count > 0 ? ExitCodes.Ok : ExitCodes.Dependency;
    }

    public static int Schema(string[] args)
    {
        Out.Json(new Dictionary<string, object?>
        {
            ["name"] = "asrgrab",
            ["version"] = "2.0.0",
            ["runtime"] = ".NET 8 / Windows 11",
            ["summary"] = "本地视频/音频逐字转写，带说话人(角色)分离，输出 Markdown + JSON。默认 JSON，面向 AI Agent。",
            ["conventions"] = new Dictionary<string, object?>
            {
                ["stdout"] = "单行 JSON（一次性命令）或 JSONL（start/transcribe 流式事件）",
                ["stderr"] = "人类可读进度；--human 时为主要输出",
                ["default_output"] = "JSON",
                ["state_dir"] = "~/.asrgrab （ASRGRAB_HOME 可覆盖）",
                ["models_dir"] = "./models （ASRGRAB_MODELS 可覆盖）",
                ["non_interactive"] = "stdin 非 TTY 时不弹交互选择，--source 缺省用默认系统声音",
            },
            ["exit_codes"] = new Dictionary<string, object?>
            {
                ["0"] = "OK",
                ["2"] = "USAGE",
                ["3"] = "SOURCE",
                ["4"] = "BUSY",
                ["5"] = "DEPENDENCY",
                ["6"] = "ENGINE",
                ["7"] = "AUDIO",
            },
            ["commands"] = new Dictionary<string, object?>
            {
                ["devices"] = new { summary = "枚举音源", options = new[] { "--kind all|loopback|input", "--human" } },
                ["start"] = new
                {
                    summary = "开始抓取并实时转写",
                    options = new[]
                    {
                        "--source <idx|id|name>", "--out <dir>", "--chunk <sec>", "--model small|full|sensevoice",
                        "--models-dir <dir>", "--no-diarize", "--record-only", "--wav <path>",
                        "--max-seconds <n>", "--num-speakers <n>", "--detach", "--human", "--non-interactive",
                    },
                    events = new[] { "session_start", "model_loading", "model_ready", "tick", "segment", "error", "session_end" },
                },
                ["stop"] = new { summary = "停止当前会话", options = new[] { "--session-id <id>" } },
                ["status"] = new { summary = "查询状态", options = new[] { "--session-id <id>", "--tail <n>" } },
                ["transcribe"] = new { summary = "离线转写文件/目录", options = new[] { "input", "--out <md|dir>", "--no-diarize", "--models-dir <dir>" } },
                ["models"] = new { summary = "查看 / 准备本地模型", options = new[] { "--download", "--full", "--sensevoice", "--dir <dir>" } },
                ["demo"] = new { summary = "生成示例转写（无需模型）", options = new[] { "--out <md|dir>" } },
                ["doctor"] = new { summary = "环境自检" },
                ["schema"] = new { summary = "输出本契约" },
            },
            ["examples"] = new[]
            {
                "asrgrab doctor",
                "asrgrab devices --human",
                "asrgrab start --source 0 --chunk 8 --detach",
                "asrgrab status --tail 20",
                "asrgrab stop",
                "asrgrab transcribe .\\video.mp4 --out .\\transcripts",
                "asrgrab demo",
            },
        });

        return ExitCodes.Ok;
    }

    public static int Models(string[] args)
    {
        var ap = new ArgParser(args);
        var dir = DefaultModelsDir(ap.Get("dir") ?? ap.Get("models-dir"));
        var paths = new ModelPaths(dir);
        var full = ap.Flag("full");
        var sense = ap.Flag("sensevoice");

        if (ap.Flag("download"))
        {
            if (Out.Human)
            {
                Console.Error.WriteLine($"下载模型到: {dir}");
                ModelManager.Ensure(paths, msg => Console.Error.WriteLine(msg), full, sense);
            }
            else
            {
                Out.Json(new { @event = "download_start", dir, full, sensevoice = sense });
                ModelManager.Ensure(paths, _ => { }, full, sense);
            }
        }

        if (Out.Human)
        {
            Console.Error.WriteLine($"模型目录: {dir}");
            Console.Error.WriteLine($"  paraformer-small {(paths.HasParaformerSmall ? "✓" : "✗")}");
            Console.Error.WriteLine($"  paraformer-full  {(paths.HasParaformer ? "✓" : "✗")}");
            Console.Error.WriteLine($"  sensevoice       {(paths.HasSenseVoice ? "✓" : "✗")}");
            Console.Error.WriteLine($"  说话人分离       {(paths.HasDiarization ? "✓" : "✗")}");
            if (!paths.HasAnyAsr) Console.Error.WriteLine("运行: asrgrab models --download");
        }
        else
        {
            Out.Json(new Dictionary<string, object?>
            {
                ["ok"] = paths.HasAnyAsr,
                ["models_dir"] = dir,
                ["status"] = paths.Status(),
            });
        }

        return paths.HasAnyAsr ? ExitCodes.Ok : ExitCodes.Dependency;
    }

    public static int Demo(string[] args)
    {
        var ap = new ArgParser(args);
        var outArg = ap.Get("out");
        var mdPath = string.IsNullOrWhiteSpace(outArg)
            ? Path.Combine("transcripts", $"demo_{DateTime.Now:yyyyMMdd_HHmmss}.md")
            : outArg.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                ? outArg
                : Path.Combine(outArg, $"demo_{DateTime.Now:yyyyMMdd_HHmmss}.md");

        var meta = new Dictionary<string, object?>
        {
            ["created_at"] = SessionStore.NowIso(),
            ["engine"] = "demo (no model)",
            ["source"] = new AudioSource { Kind = "demo", Name = "示例音源" },
            ["mode"] = "demo",
        };

        using (var w = new TranscriptWriter(mdPath, meta))
        {
            w.Append(new List<Segment>
            {
                new() { StartMs = 0, EndMs = 3200, Speaker = "角色A", Text = "大家好，今天聊聊本地语音转写。" },
                new() { StartMs = 3200, EndMs = 7600, Speaker = "角色B", Text = "为什么要本地？因为音频不想出这台机器。" },
                new() { StartMs = 7600, EndMs = 12000, Speaker = "角色A", Text = "对，而且角色对话要能分清谁在说。" },
                new() { StartMs = 12000, EndMs = 15800, Speaker = "角色C", Text = "最后保存成 Markdown，AI 直接能读。" },
            });
        }

        var md = Path.GetFullPath(mdPath);
        var json = Path.ChangeExtension(md, ".json");

        if (Out.Human)
        {
            Console.Error.WriteLine("✓ 示例转写已生成");
            Console.Error.WriteLine(md);
        }
        else
        {
            Out.Json(new { ok = true, out_file = md, json_file = json, segments = 4 });
        }

        return ExitCodes.Ok;
    }

    public static int RunDaemon(string[] args)
    {
        var ap = new ArgParser(args);
        var cfgPath = ap.Get("config")
                      ?? throw new CliError(ExitCodes.Usage, "USAGE", "_run 需要 --config");

        var sdir = Path.GetDirectoryName(Path.GetFullPath(cfgPath))!;
        var logPath = Path.Combine(sdir, "daemon.log");
        var log = new StreamWriter(logPath, append: true, new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(log);
        Console.SetError(log);

        var cfg = JsonIo.Read<SessionConfig>(cfgPath)
                  ?? throw new CliError(ExitCodes.Usage, "USAGE", "配置读取失败: " + cfgPath);

        SessionRunner.Run(cfg, ev => Out.Json(ev));
        return ExitCodes.Ok;
    }
}
