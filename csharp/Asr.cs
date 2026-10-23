using System.Diagnostics;
using NAudio.Wave;
using SherpaOnnx;

namespace AsrGrab;

public sealed class ModelPaths
{
    public string Dir { get; }

    public ModelPaths(string dir) => Dir = Path.GetFullPath(dir);

    public string ParaformerDir => Path.Combine(Dir, "sherpa-onnx-paraformer-zh-2024-03-09");
    public string ParaformerModel => Path.Combine(ParaformerDir, "model.onnx");
    public string ParaformerTokens => Path.Combine(ParaformerDir, "tokens.txt");

    public string ParaformerSmallDir => Path.Combine(Dir, "sherpa-onnx-paraformer-zh-small-2024-03-09");
    public string ParaformerSmallModel => Path.Combine(ParaformerSmallDir, "model.int8.onnx");
    public string ParaformerSmallTokens => Path.Combine(ParaformerSmallDir, "tokens.txt");

    public string SenseVoiceDir => Path.Combine(Dir, "sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17");
    public string SenseVoiceModel => Path.Combine(SenseVoiceDir, "model.onnx");
    public string SenseVoiceTokens => Path.Combine(SenseVoiceDir, "tokens.txt");

    public string SegModel => Path.Combine(Dir, "sherpa-onnx-pyannote-segmentation-3-0", "model.onnx");
    public string EmbModel => Path.Combine(Dir, "3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx");

    public bool HasParaformer => File.Exists(ParaformerModel) && File.Exists(ParaformerTokens);
    public bool HasParaformerSmall => File.Exists(ParaformerSmallModel) && File.Exists(ParaformerSmallTokens);
    public bool HasSenseVoice => File.Exists(SenseVoiceModel) && File.Exists(SenseVoiceTokens);
    public bool HasDiarization => File.Exists(SegModel) && File.Exists(EmbModel);
    public bool HasAnyAsr => HasParaformerSmall || HasParaformer || HasSenseVoice;

    public object Status() => new
    {
        dir = Dir,
        asr_paraformer_small = HasParaformerSmall,
        asr_paraformer_full = HasParaformer,
        asr_sensevoice = HasSenseVoice,
        diarization = HasDiarization,
        paraformer_small_model = ParaformerSmallModel,
        paraformer_model = ParaformerModel,
        sensevoice_model = SenseVoiceModel,
        segmentation_model = SegModel,
        embedding_model = EmbModel,
    };
}

public static class ModelManager
{
    private const string DefaultBase = "https://github.com/k2-fsa/sherpa-onnx/releases/download";

    private static List<string> BaseUrls()
    {
        var env = Environment.GetEnvironmentVariable("ASRGRAB_MODEL_BASE");
        if (!string.IsNullOrWhiteSpace(env))
            return new List<string> { env.TrimEnd('/') };

        // GitHub 直连优先；国内网络失败时自动走镜像（gh-proxy 实测较快）
        return new List<string>
        {
            DefaultBase,
            "https://gh-proxy.com/" + DefaultBase,
            "https://ghproxy.net/" + DefaultBase,
        };
    }

    private sealed record Asset(string Name, string Relative, bool IsTarBz2, bool Present);

    public static void Ensure(ModelPaths paths, Action<string> log, bool full = false, bool sensevoice = false, bool needDiarization = true)
    {
        Directory.CreateDirectory(paths.Dir);
        var assets = new List<Asset>();

        if (full)
            assets.Add(new("paraformer-zh-full",
                "asr-models/sherpa-onnx-paraformer-zh-2024-03-09.tar.bz2",
                true, paths.HasParaformer));
        else
            assets.Add(new("paraformer-zh-small",
                "asr-models/sherpa-onnx-paraformer-zh-small-2024-03-09.tar.bz2",
                true, paths.HasParaformerSmall));

        if (sensevoice)
            assets.Add(new("sensevoice",
                "asr-models/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17.tar.bz2",
                true, paths.HasSenseVoice));

        // 说话人分离模型较大，仅在需要时才拉取（--no-diarize / 仅录音时跳过）
        if (needDiarization)
        {
            assets.Add(new("pyannote-segmentation",
                "speaker-segmentation-models/sherpa-onnx-pyannote-segmentation-3-0.tar.bz2",
                true, File.Exists(paths.SegModel)));
            assets.Add(new("3d-speaker-embedding",
                "speaker-recongition-models/3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx",
                false, File.Exists(paths.EmbModel)));
        }

        var bases = BaseUrls();

        foreach (var a in assets)
        {
            if (a.Present)
            {
                log($"✓ {a.Name} 已存在，跳过");
                continue;
            }

            log($"↓ {a.Name}");
            var tmp = Path.Combine(paths.Dir, a.Name + (a.IsTarBz2 ? ".tar.bz2" : ".onnx.download"));

            var ok = false;
            var tried = new List<string>();
            foreach (var b in bases)
            {
                var url = $"{b}/{a.Relative}";
                tried.Add(url);
                try
                {
                    Download(url, tmp, log);
                    ok = true;
                    break;
                }
                catch (CliError e)
                {
                    log($"  ! {e.Message}");
                    TryDelete(tmp);
                }
            }

            if (!ok)
            {
                throw new CliError(ExitCodes.Engine, "ENGINE",
                    $"模型下载失败：{a.Name}（已尝试 {tried.Count} 个源）", new()
                    {
                        ["urls"] = tried,
                        ["hint"] = "可设置 ASRGRAB_MODEL_BASE 指向可用镜像；或手动下载后放到 models/ 对应目录",
                    });
            }

            if (a.IsTarBz2)
            {
                ExtractTarBz2(tmp, paths.Dir);
                TryDelete(tmp);
            }
            else
            {
                var finalName = Path.GetFileName(new Uri(a.Relative).AbsolutePath);
                var dest = Path.Combine(paths.Dir, finalName);
                File.Move(tmp, dest, overwrite: true);
            }
        }

        log("");
        log("模型状态:");
        if (!paths.HasAnyAsr) log("  ✗ 没有可用的 ASR 模型");
        if (paths.HasParaformerSmall) log("  ✓ paraformer-small");
        if (paths.HasParaformer) log("  ✓ paraformer-full");
        if (paths.HasSenseVoice) log("  ✓ sensevoice");
        if (!paths.HasDiarization) log("  ✗ 说话人分离模型仍缺失");
        else log("  ✓ 说话人分离就绪");
    }

    private static void Download(string url, string dest, Action<string> log)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = true };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("asrgrab/2.0");

        try
        {
            using var resp = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
                .GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode)
            {
                throw new CliError(ExitCodes.Engine, "ENGINE",
                    $"模型下载失败: HTTP {(int)resp.StatusCode} {url}", new()
                    {
                        ["url"] = url,
                        ["hint"] = "可设置 ASRGRAB_MODEL_BASE 指向镜像；或手动下载放到 models/ 下",
                    });
            }

            var total = resp.Content.Headers.ContentLength ?? -1;
            using var src = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
            using var dst = File.Create(dest);
            var buffer = new byte[81920];
            long read = 0;
            var last = DateTime.UtcNow;
            int n;
            while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
            {
                dst.Write(buffer, 0, n);
                read += n;
                if ((DateTime.UtcNow - last).TotalSeconds >= 2)
                {
                    last = DateTime.UtcNow;
                    log(total > 0
                        ? $"    {read / 1048576.0:0.0} / {total / 1048576.0:0.0} MB"
                        : $"    {read / 1048576.0:0.0} MB");
                }
            }
        }
        catch (CliError) { throw; }
        catch (Exception e)
        {
            TryDelete(dest);
            throw new CliError(ExitCodes.Engine, "ENGINE", $"模型下载异常: {e.Message}", new()
            {
                ["url"] = url,
                ["hint"] = "检查网络/代理，或设置 ASRGRAB_MODEL_BASE 指向镜像",
            });
        }
    }

    private static void ExtractTarBz2(string archive, string destDir)
    {
        var psi = new ProcessStartInfo("tar")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add("-xjf");
        psi.ArgumentList.Add(archive);
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(destDir);

        using var p = Process.Start(psi)
                      ?? throw new CliError(ExitCodes.Engine, "ENGINE", "无法启动 tar 解压");
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new CliError(ExitCodes.Engine, "ENGINE", "解压失败: " + err.Trim(), new() { ["archive"] = archive });
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

public sealed class SherpaEngine : IDisposable
{
    private const double MinSegmentDb = -50.0;
    private const double MinSegmentSec = 0.25;

    private readonly OfflineRecognizer _recognizer;
    private readonly OfflineSpeakerDiarization? _diarizer;

    public static string NormalizeKind(string modelKind)
    {
        var m = (modelKind ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(m)) return "small";
        if (m.Contains("sense")) return "sensevoice";
        if (m.Contains("small")) return "small";
        if (m is "paraformer" or "paraformer-zh" or "paraformer-full" or "full") return "full";
        return "small";
    }

    public SherpaEngine(ModelPaths paths, string modelKind, bool diarize, int numSpeakers, int threads = 2, Action<string>? log = null)
    {
        var logFn = log ?? (s => Console.Error.WriteLine(s));
        var kind = NormalizeKind(modelKind);

        // 首次使用时自动准备缺失的本地模型：仅首次需联网，内置多镜像回退；
        // 若离线或镜像不可达，Ensure 会抛出带手动准备提示的异常，不会静默失败。
        ModelManager.Ensure(paths, logFn, full: kind == "full", sensevoice: kind == "sensevoice", needDiarization: diarize);

        string modelPath, tokensPath;
        bool ready;
        if (kind == "sensevoice")
        {
            modelPath = paths.SenseVoiceModel;
            tokensPath = paths.SenseVoiceTokens;
            ready = paths.HasSenseVoice;
        }
        else if (kind == "full")
        {
            modelPath = paths.ParaformerModel;
            tokensPath = paths.ParaformerTokens;
            ready = paths.HasParaformer;
        }
        else
        {
            modelPath = paths.ParaformerSmallModel;
            tokensPath = paths.ParaformerSmallTokens;
            ready = paths.HasParaformerSmall;
        }

        if (!ready)
        {
            var flag = kind == "full" ? " --full" : kind == "sensevoice" ? " --sensevoice" : "";
            throw new CliError(ExitCodes.Dependency, "DEPENDENCY",
                $"ASR 模型缺失（{kind}）。运行: asrgrab models --download{flag}", new()
                {
                    ["dependency"] = "models",
                    ["model"] = kind,
                    ["models_dir"] = paths.Dir,
                });
        }

        var cfg = new OfflineRecognizerConfig();
        cfg.FeatConfig.SampleRate = 16000;
        cfg.FeatConfig.FeatureDim = 80;
        cfg.ModelConfig.NumThreads = Math.Max(1, threads);
        cfg.ModelConfig.Provider = "cpu";
        cfg.ModelConfig.Debug = 0;
        cfg.ModelConfig.Tokens = tokensPath;

        if (kind == "sensevoice")
        {
            cfg.ModelConfig.SenseVoice.Model = modelPath;
            cfg.ModelConfig.SenseVoice.Language = "zh";
            cfg.ModelConfig.SenseVoice.UseInverseTextNormalization = 1;
        }
        else
        {
            cfg.ModelConfig.Paraformer.Model = modelPath;
        }

        _recognizer = new OfflineRecognizer(cfg);

        if (diarize)
        {
            if (!paths.HasDiarization)
            {
                throw new CliError(ExitCodes.Dependency, "DEPENDENCY",
                    "说话人分离模型缺失。运行: asrgrab models --download；或加 --no-diarize", new()
                    {
                        ["dependency"] = "diarization-models",
                        ["models_dir"] = paths.Dir,
                    });
            }

            var dc = new OfflineSpeakerDiarizationConfig();
            dc.Segmentation.Pyannote.Model = paths.SegModel;
            dc.Segmentation.Pyannote.WindowShiftRatio = 0.1f;
            dc.Embedding.Model = paths.EmbModel;
            dc.Embedding.NumThreads = Math.Max(1, threads);
            dc.Clustering.NumClusters = numSpeakers > 0 ? numSpeakers : -1;
            dc.Clustering.Threshold = 0.75f;
            _diarizer = new OfflineSpeakerDiarization(dc);
        }
    }

    public List<Segment> Transcribe(float[] samples, int sampleRate, bool diarize)
    {
        if (sampleRate != 16000) samples = PcmUtil.Resample(samples, sampleRate, 16000);
        if (samples.Length < 1600) return new List<Segment>(); // < 0.1s
        if (PcmUtil.Dbfs(samples) < -55.0) return new List<Segment>(); // 整段近乎静音

        if (!diarize || _diarizer == null)
        {
            var text = Recognize(samples);
            return string.IsNullOrWhiteSpace(text)
                ? new List<Segment>()
                : new List<Segment>
                {
                    new()
                    {
                        StartMs = 0,
                        EndMs = (int)(samples.Length * 1000.0 / 16000),
                        Speaker = null,
                        Text = text.Trim(),
                    },
                };
        }

        var raw = _diarizer.Process(samples)
            .OrderBy(s => s.Start)
            .ToArray();

        var result = new List<Segment>();
        if (raw.Length == 0)
        {
            var whole = Recognize(samples);
            if (!string.IsNullOrWhiteSpace(whole))
            {
                result.Add(new Segment
                {
                    StartMs = 0,
                    EndMs = (int)(samples.Length * 1000.0 / 16000),
                    Speaker = null,
                    Text = whole.Trim(),
                });
            }
            return result;
        }

        foreach (var s in raw)
        {
            var start = (int)Math.Clamp(s.Start * 16000.0, 0, samples.Length);
            var end = (int)Math.Clamp(s.End * 16000.0, 0, samples.Length);
            if (end <= start) continue;
            if (end - start < (int)(16000 * MinSegmentSec)) continue;

            var slice = new float[end - start];
            Array.Copy(samples, start, slice, 0, slice.Length);
            if (PcmUtil.Dbfs(slice) < MinSegmentDb) continue; // 过滤静音/噪声段，避免“我我我我”幻觉

            var text = Recognize(slice);
            if (string.IsNullOrWhiteSpace(text)) continue;

            result.Add(new Segment
            {
                StartMs = (int)(s.Start * 1000),
                EndMs = (int)(s.End * 1000),
                Speaker = SpeakerLabel(s.Speaker),
                Text = text.Trim(),
            });
        }

        return result;
    }

    private string Recognize(float[] samples)
    {
        using var stream = _recognizer.CreateStream();
        stream.AcceptWaveform(16000, samples);
        _recognizer.Decode(stream);
        return stream.Result.Text ?? "";
    }

    public static string SpeakerLabel(int speaker)
        => $"角色{(char)('A' + Math.Abs(speaker) % 26)}";

    public void Dispose()
    {
        _recognizer.Dispose();
        _diarizer?.Dispose();
    }
}
