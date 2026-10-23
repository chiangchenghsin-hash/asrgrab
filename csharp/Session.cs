using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NAudio.Wave;

namespace AsrGrab;

public sealed class SessionConfig
{
    public string SessionId { get; set; } = "";
    public AudioSource Source { get; set; } = new();
    public string OutDir { get; set; } = "transcripts";
    public int ChunkSec { get; set; } = 12;
    public string Model { get; set; } = "paraformer";
    public string ModelsDir { get; set; } = "models";
    public bool NoDiarize { get; set; }
    public bool RecordOnly { get; set; }
    public string? WavPath { get; set; }
    public int MaxSeconds { get; set; }
    public int NumSpeakers { get; set; }
    public string? MdPath { get; set; }
}

public sealed class CurrentSession
{
    public string SessionId { get; set; } = "";
    public int Pid { get; set; }
    public string SessionDir { get; set; } = "";
    public string StartedAt { get; set; } = "";
}

public sealed class TranscriptFile
{
    [JsonPropertyName("segments")] public List<Segment> Segments { get; set; } = new();
}

public static class SessionStore
{
    public static string Root
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("ASRGRAB_HOME");
            var root = string.IsNullOrWhiteSpace(env)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".asrgrab")
                : env;
            Directory.CreateDirectory(root);
            return root;
        }
    }

    public static string SessionsDir
    {
        get
        {
            var d = Path.Combine(Root, "sessions");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    public static string CurrentFile => Path.Combine(Root, "current.json");

    public static string SessionDir(string id) => Path.Combine(SessionsDir, id);

    public static string NowId()
        => DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..6];

    public static string NowIso()
        => DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz");

    public static void WriteJson(string path, object? value, bool pretty = false)
        => JsonIo.Write(path, value, pretty);

    public static CurrentSession? ReadCurrent() => JsonIo.Read<CurrentSession>(CurrentFile);

    public static void WriteCurrent(string sessionId, int pid, string sessionDir)
        => JsonIo.Write(CurrentFile, new CurrentSession
        {
            SessionId = sessionId,
            Pid = pid,
            SessionDir = sessionDir,
            StartedAt = NowIso(),
        });

    public static void ClearCurrent(string sessionId)
    {
        var cur = ReadCurrent();
        if (cur != null && cur.SessionId == sessionId)
        {
            try { File.Delete(CurrentFile); } catch { }
        }
    }

    public static bool PidAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    public static string? GetStr(Dictionary<string, object?> d, string key)
    {
        if (!d.TryGetValue(key, out var v) || v is null) return null;
        if (v is JsonElement je)
        {
            return je.ValueKind switch
            {
                JsonValueKind.String => je.GetString(),
                JsonValueKind.Null => null,
                _ => je.ToString(),
            };
        }

        return v.ToString();
    }

    public static int GetInt(Dictionary<string, object?> d, string key)
        => int.TryParse(GetStr(d, key), out var v) ? v : 0;

    private static Dictionary<string, object?> Idle(string message = "没有正在运行的会话")
        => new() { ["ok"] = true, ["state"] = "idle", ["message"] = message };

    public static Dictionary<string, object?> GetStatus(string? sessionId, int tail)
    {
        string? sdir;
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            sdir = SessionDir(sessionId);
        }
        else
        {
            var cur = ReadCurrent();
            if (cur == null) return Idle();
            sdir = string.IsNullOrWhiteSpace(cur.SessionDir) ? SessionDir(cur.SessionId) : cur.SessionDir;
        }

        var statusPath = Path.Combine(sdir, "status.json");
        var st = JsonIo.Read<Dictionary<string, object?>>(statusPath);
        if (st == null)
            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["state"] = "idle",
                ["message"] = "会话不存在或已清理",
                ["session_dir"] = sdir,
            };

        var state = GetStr(st, "state");
        if ((state is "running" or "loading_model" or "starting") && !PidAlive(GetInt(st, "pid")))
        {
            st["state"] = "stale";
            st["message"] = "进程已退出（状态文件残留）";
        }

        if (tail > 0 && GetStr(st, "json_file") is { Length: > 0 } jf && File.Exists(jf))
        {
            var tf = JsonIo.Read<TranscriptFile>(jf);
            var segs = tf?.Segments ?? new List<Segment>();
            st["tail"] = segs.Skip(Math.Max(0, segs.Count - tail)).ToList();
        }

        return st;
    }

    public static Dictionary<string, object?> Stop(string? sessionId, int waitSec)
    {
        string? sdir;
        string? sid = sessionId;
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            sdir = SessionDir(sessionId);
        }
        else
        {
            var cur = ReadCurrent();
            if (cur == null) return Idle();
            sdir = string.IsNullOrWhiteSpace(cur.SessionDir) ? SessionDir(cur.SessionId) : cur.SessionDir;
            sid = cur.SessionId;
        }

        if (!Directory.Exists(sdir)) return Idle("会话不存在");

        var statusPath = Path.Combine(sdir, "status.json");
        var st = JsonIo.Read<Dictionary<string, object?>>(statusPath) ?? new Dictionary<string, object?>();

        if ((GetStr(st, "state") is "stopped" or "error") || !PidAlive(GetInt(st, "pid")))
        {
            st["ok"] = true;
            return st;
        }

        File.WriteAllText(Path.Combine(sdir, "stop.flag"), "stop");
        var deadline = DateTime.UtcNow.AddSeconds(waitSec);
        while (DateTime.UtcNow < deadline)
        {
            st = JsonIo.Read<Dictionary<string, object?>>(statusPath) ?? st;
            var state = GetStr(st, "state");
            if ((state is "stopped" or "error") || !PidAlive(GetInt(st, "pid")))
            {
                st["ok"] = true;
                return st;
            }

            Thread.Sleep(500);
        }

        return new Dictionary<string, object?>
        {
            ["ok"] = false,
            ["state"] = "stopping",
            ["session_id"] = sid,
            ["message"] = $"{waitSec}s 内未完全停止，可能仍在收尾（推理中）",
            ["status"] = st,
        };
    }
}

public sealed class EventSink : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly Action<Dictionary<string, object?>>? _console;

    public EventSink(string path, Action<Dictionary<string, object?>>? console)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _writer = new StreamWriter(path, append: true, new UTF8Encoding(false)) { AutoFlush = true };
        _console = console;
    }

    public void Emit(Dictionary<string, object?> ev)
    {
        ev["ts"] = SessionStore.NowIso();
        _writer.WriteLine(JsonIo.Serialize(ev));
        _console?.Invoke(ev);
    }

    public void Dispose() => _writer.Dispose();
}

public static class SessionRunner
{
    private sealed record AudioChunk(float[] Data, double OffsetSec);

    private static float[] Concat(List<float[]> parts, int total)
    {
        var arr = new float[total];
        var offset = 0;
        foreach (var p in parts)
        {
            Array.Copy(p, 0, arr, offset, p.Length);
            offset += p.Length;
        }

        return arr;
    }

    public static Dictionary<string, object?> Run(
        SessionConfig cfg,
        Action<Dictionary<string, object?>>? console)
    {
        var sdir = SessionStore.SessionDir(cfg.SessionId);
        Directory.CreateDirectory(sdir);
        var statusPath = Path.Combine(sdir, "status.json");
        var stopFlag = Path.Combine(sdir, "stop.flag");
        if (File.Exists(stopFlag)) File.Delete(stopFlag);

        using var sink = new EventSink(Path.Combine(sdir, "events.jsonl"), console);
        using var cts = new CancellationTokenSource();

        var outDir = Path.GetFullPath(string.IsNullOrWhiteSpace(cfg.OutDir) ? "transcripts" : cfg.OutDir);
        Directory.CreateDirectory(outDir);

        var mdPath = string.IsNullOrWhiteSpace(cfg.MdPath)
            ? Path.Combine(outDir, $"transcript_{cfg.SessionId}.md")
            : Path.GetFullPath(cfg.MdPath);

        string? wavPath = cfg.RecordOnly
            ? (string.IsNullOrWhiteSpace(cfg.WavPath)
                ? Path.Combine(outDir, $"recording_{cfg.SessionId}.wav")
                : Path.GetFullPath(cfg.WavPath))
            : null;

        if (wavPath != null)
        {
            var d = Path.GetDirectoryName(wavPath);
            if (!string.IsNullOrEmpty(d)) Directory.CreateDirectory(d);
        }

        var engineName = cfg.RecordOnly
            ? "record-only"
            : $"sherpa-onnx {cfg.Model}" + (cfg.NoDiarize ? "" : " + pyannote/3dspeaker diarization");

        var meta = new Dictionary<string, object?>
        {
            ["session_id"] = cfg.SessionId,
            ["created_at"] = SessionStore.NowIso(),
            ["engine"] = engineName,
            ["source"] = cfg.Source,
            ["mode"] = cfg.RecordOnly ? "record_only" : "transcribe",
        };

        TranscriptWriter? writer = cfg.RecordOnly ? null : new TranscriptWriter(mdPath, meta);

        var state = new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["session_id"] = cfg.SessionId,
            ["state"] = "starting",
            ["pid"] = Environment.ProcessId,
            ["source"] = cfg.Source.Public(),
            ["out_file"] = cfg.RecordOnly ? null : mdPath,
            ["json_file"] = cfg.RecordOnly ? null : Path.ChangeExtension(mdPath, ".json"),
            ["wav_file"] = wavPath,
            ["record_only"] = cfg.RecordOnly,
            ["started_at"] = SessionStore.NowIso(),
            ["elapsed_s"] = 0.0,
            ["bytes"] = 0L,
            ["segments"] = 0,
            ["level_db"] = -120.0,
            ["chunk_sec"] = cfg.ChunkSec,
        };

        void WriteStatus()
        {
            state["updated_at"] = SessionStore.NowIso();
            SessionStore.WriteJson(statusPath, state);
        }

        SessionStore.WriteJson(Path.Combine(sdir, "session.json"), new Dictionary<string, object?>
        {
            ["session_id"] = cfg.SessionId,
            ["pid"] = Environment.ProcessId,
            ["session_dir"] = sdir,
            ["source"] = cfg.Source.Public(),
            ["out_dir"] = outDir,
            ["md_path"] = cfg.RecordOnly ? null : mdPath,
            ["wav_path"] = wavPath,
            ["record_only"] = cfg.RecordOnly,
            ["chunk_sec"] = cfg.ChunkSec,
            ["model"] = cfg.Model,
            ["started_at"] = SessionStore.NowIso(),
        });
        SessionStore.WriteCurrent(cfg.SessionId, Environment.ProcessId, sdir);
        WriteStatus();

        sink.Emit(new Dictionary<string, object?>
        {
            ["event"] = "session_start",
            ["session_id"] = cfg.SessionId,
            ["pid"] = Environment.ProcessId,
            ["source"] = cfg.Source.Public(),
            ["source_name"] = cfg.Source.Name,
            ["source_kind"] = cfg.Source.Kind,
            ["out_file"] = state["out_file"],
            ["wav_file"] = wavPath,
            ["record_only"] = cfg.RecordOnly,
            ["chunk_sec"] = cfg.ChunkSec,
        });

        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                if (File.Exists(stopFlag))
                {
                    cts.Cancel();
                    return;
                }

                try { await Task.Delay(500, cts.Token); }
                catch { return; }
            }
        });

        SherpaEngine? engine = null;
        if (!cfg.RecordOnly)
        {
            state["state"] = "loading_model";
            WriteStatus();
            sink.Emit(new Dictionary<string, object?>
            {
                ["event"] = "model_loading",
                ["model"] = cfg.Model,
                ["models_dir"] = cfg.ModelsDir,
            });

            try
            {
                engine = new SherpaEngine(new ModelPaths(cfg.ModelsDir), cfg.Model, !cfg.NoDiarize, cfg.NumSpeakers, 2);
            }
            catch (CliError e)
            {
                state["state"] = "error";
                state["error"] = new Dictionary<string, object?> { ["code"] = e.Code, ["message"] = e.Message };
                WriteStatus();
                sink.Emit(new Dictionary<string, object?>
                {
                    ["event"] = "error",
                    ["code"] = e.Code,
                    ["message"] = e.Message,
                });
                writer?.Dispose();
                SessionStore.ClearCurrent(cfg.SessionId);
                throw;
            }

            sink.Emit(new Dictionary<string, object?> { ["event"] = "model_ready", ["model"] = cfg.Model });
            state["state"] = "running";
            WriteStatus();
        }

        using var frameQ = new BlockingCollection<float[]>(400);
        BlockingCollection<AudioChunk>? inferQ = cfg.RecordOnly
            ? null
            : new BlockingCollection<AudioChunk>(new ConcurrentQueue<AudioChunk>(), 8);

        Thread? worker = null;
        if (!cfg.RecordOnly)
        {
            worker = new Thread(() =>
            {
                foreach (var chunk in inferQ!.GetConsumingEnumerable())
                {
                    try
                    {
                        var segs = engine!.Transcribe(chunk.Data, 16000, !cfg.NoDiarize);
                        if (segs.Count == 0) continue;

                        // 跨 chunk 累加时间轴，避免每段都从 00:00 开始
                        var offsetMs = (int)Math.Round(chunk.OffsetSec * 1000);
                        foreach (var s in segs)
                        {
                            s.StartMs += offsetMs;
                            s.EndMs += offsetMs;
                        }

                        writer!.Append(segs);
                        state["segments"] = writer.SegmentCount;
                        foreach (var s in segs)
                        {
                            sink.Emit(new Dictionary<string, object?>
                            {
                                ["event"] = "segment",
                                ["start_ms"] = s.StartMs,
                                ["end_ms"] = s.EndMs,
                                ["speaker"] = s.Speaker,
                                ["text"] = s.Text,
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        sink.Emit(new Dictionary<string, object?>
                        {
                            ["event"] = "error",
                            ["code"] = "ENGINE",
                            ["message"] = ex.Message,
                        });
                    }
                }
            })
            {
                IsBackground = true,
                Name = "asrgrab-infer",
            };
            worker.Start();
        }

        WaveFileWriter? wav = null;
        if (cfg.RecordOnly) wav = new WaveFileWriter(wavPath!, new WaveFormat(16000, 16, 1));

        using var recorder = new WasapiRecorder(
            cfg.Source,
            16000,
            f =>
            {
                try { frameQ.Add(f); } catch { }
            },
            ex =>
            {
                sink.Emit(new Dictionary<string, object?>
                {
                    ["event"] = "error",
                    ["code"] = "AUDIO",
                    ["message"] = ex.Message,
                });
                cts.Cancel();
            });

        try
        {
            recorder.Start();
        }
        catch (CliError e)
        {
            state["state"] = "error";
            state["error"] = new Dictionary<string, object?> { ["code"] = e.Code, ["message"] = e.Message };
            WriteStatus();
            sink.Emit(new Dictionary<string, object?>
            {
                ["event"] = "error",
                ["code"] = e.Code,
                ["message"] = e.Message,
            });
            wav?.Dispose();
            writer?.Dispose();
            SessionStore.ClearCurrent(cfg.SessionId);
            throw;
        }

        if (SessionStore.GetStr(state, "state") == "starting")
        {
            state["state"] = "running";
            WriteStatus();
        }

        var chunkSamples = Math.Max(1, cfg.ChunkSec * 16000);
        var buf = new List<float[]>();
        var buffered = 0;
        var processedSec = 0.0;
        long bytes = 0;
        var started = DateTime.UtcNow;
        double lastTick = 0, lastEvent = 0;
        var db = -120.0;

        try
        {
            while (!cts.IsCancellationRequested)
            {
                if (cfg.MaxSeconds > 0 && (DateTime.UtcNow - started).TotalSeconds >= cfg.MaxSeconds) break;

                if (frameQ.TryTake(out var frame, 250, cts.Token))
                {
                    bytes += frame.Length * 2L;
                    state["bytes"] = bytes;
                    db = PcmUtil.Dbfs(frame);
                    state["level_db"] = db;

                    if (cfg.RecordOnly)
                    {
                        var b = PcmUtil.ToPcm16Bytes(frame);
                        wav!.Write(b, 0, b.Length);
                    }
                    else
                    {
                        buf.Add(frame);
                        buffered += frame.Length;
                        if (buffered >= chunkSamples)
                        {
                            var data = Concat(buf, buffered);
                            var offset = processedSec;
                            processedSec += data.Length / 16000.0;
                            buf.Clear();
                            buffered = 0;
                            inferQ!.Add(new AudioChunk(data, offset));
                        }
                    }
                }

                var now = (DateTime.UtcNow - started).TotalSeconds;
                if (now - lastTick >= 1.0)
                {
                    lastTick = now;
                    state["elapsed_s"] = Math.Round(now, 1);
                    WriteStatus();

                    if (Out.Human)
                    {
                        console?.Invoke(new Dictionary<string, object?>
                        {
                            ["event"] = "ui_tick",
                            ["elapsed_s"] = Math.Round(now, 1),
                            ["level_db"] = db,
                            ["bytes"] = bytes,
                            ["segments"] = state["segments"],
                        });
                    }
                }

                if (now - lastEvent >= 5.0)
                {
                    lastEvent = now;
                    sink.Emit(new Dictionary<string, object?>
                    {
                        ["event"] = "tick",
                        ["elapsed_s"] = Math.Round(now, 1),
                        ["bytes"] = bytes,
                        ["segments"] = state["segments"],
                        ["level_db"] = db,
                    });
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopped
        }
        finally
        {
            try { recorder.Stop(); } catch { }

            if (cfg.RecordOnly)
            {
                while (frameQ.TryTake(out var f))
                {
                    var b = PcmUtil.ToPcm16Bytes(f);
                    wav!.Write(b, 0, b.Length);
                }

                wav?.Dispose();
            }
            else
            {
                while (frameQ.TryTake(out var f))
                {
                    buf.Add(f);
                    buffered += f.Length;
                }

                if (buffered > 0) inferQ!.Add(new AudioChunk(Concat(buf, buffered), processedSec));
                inferQ!.CompleteAdding();
                worker?.Join(TimeSpan.FromMinutes(5));
                writer?.Dispose();
                inferQ.Dispose();
            }

            state["state"] = "stopped";
            state["elapsed_s"] = Math.Round((DateTime.UtcNow - started).TotalSeconds, 1);
            WriteStatus();

            sink.Emit(new Dictionary<string, object?>
            {
                ["event"] = "session_end",
                ["reason"] = "stopped",
                ["elapsed_s"] = state["elapsed_s"],
                ["segments"] = state["segments"],
                ["out_file"] = state["out_file"],
                ["wav_file"] = wavPath,
            });

            SessionStore.ClearCurrent(cfg.SessionId);
            try { if (File.Exists(stopFlag)) File.Delete(stopFlag); } catch { }
            engine?.Dispose();
        }

        return state;
    }
}
