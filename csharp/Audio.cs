using System.Diagnostics;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AsrGrab;

public sealed class AudioSource
{
    public int Index { get; set; }
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "loopback"; // loopback | input
    public string Name { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public int Channels { get; set; }
    public bool IsDefault { get; set; }

    public object Public() => new
    {
        index = Index,
        id = Id,
        kind = Kind,
        name = Name,
        channels = Channels,
        is_default = IsDefault,
        recommended = Kind == "loopback" && IsDefault,
    };
}

public static class AudioSourceEnumerator
{
    public static List<AudioSource> List()
    {
        var result = new List<AudioSource>();
        try
        {
            using var en = new MMDeviceEnumerator();
            string? defRender = null, defCapture = null;
            try
            {
                using var d = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                defRender = d.ID;
            }
            catch { /* no default render */ }

            try
            {
                using var d = en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                defCapture = d.ID;
            }
            catch { /* no default capture */ }

            var li = 0;
            try
            {
                foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    var ch = 2;
                    try { ch = d.AudioClient.MixFormat.Channels; } catch { }
                    result.Add(new AudioSource
                    {
                        Index = result.Count,
                        Id = $"loopback:{li++}",
                        Kind = "loopback",
                        Name = d.FriendlyName,
                        DeviceId = d.ID,
                        Channels = ch,
                        IsDefault = d.ID == defRender,
                    });
                }
            }
            catch { /* render enumeration failed */ }

            var ii = 0;
            try
            {
                foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                {
                    var ch = 1;
                    try { ch = d.AudioClient.MixFormat.Channels; } catch { }
                    result.Add(new AudioSource
                    {
                        Index = result.Count,
                        Id = $"input:{ii++}",
                        Kind = "input",
                        Name = d.FriendlyName,
                        DeviceId = d.ID,
                        Channels = ch,
                        IsDefault = d.ID == defCapture,
                    });
                }
            }
            catch { /* capture enumeration failed */ }
        }
        catch
        {
            // COM / no audio service: return empty list
        }

        return result;
    }
}

public static class SourceResolver
{
    public static AudioSource Default(List<AudioSource> sources)
    {
        if (sources.Count == 0)
            throw new CliError(ExitCodes.Source, "SOURCE", "未找到任何可用音源（检查 Windows 声音设备）");

        return sources.FirstOrDefault(s => s.Kind == "loopback" && s.IsDefault)
               ?? sources.FirstOrDefault(s => s.Kind == "loopback")
               ?? sources[0];
    }

    public static AudioSource Resolve(string? spec, List<AudioSource> sources)
    {
        if (sources.Count == 0)
            throw new CliError(ExitCodes.Source, "SOURCE", "未找到任何可用音源");

        if (string.IsNullOrWhiteSpace(spec)) return Default(sources);
        var raw = spec.Trim();

        if (int.TryParse(raw, out var idx))
        {
            var hit = sources.FirstOrDefault(s => s.Index == idx);
            if (hit != null) return hit;
            throw new CliError(ExitCodes.Source, "SOURCE", $"音源编号 {idx} 不存在", new()
            {
                ["valid_range"] = new[] { 0, sources.Count - 1 },
            });
        }

        var exact = sources.FirstOrDefault(s => s.Id.Equals(raw, StringComparison.OrdinalIgnoreCase))
                    ?? sources.FirstOrDefault(s => s.Name.Equals(raw, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;

        var matches = sources
            .Where(s => s.Name.Contains(raw, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 1) return matches[0];
        if (matches.Count == 0)
            throw new CliError(ExitCodes.Source, "SOURCE", $"找不到音源 '{raw}'。先运行: asrgrab devices", new()
            {
                ["candidates"] = sources.Select(s => s.Public()).ToList(),
            });

        throw new CliError(ExitCodes.Source, "SOURCE", $"音源 '{raw}' 匹配到 {matches.Count} 个，请用编号或完整 id", new()
        {
            ["candidates"] = matches.Select(s => s.Public()).ToList(),
        });
    }
}

public static class SourcePicker
{
    private static void SafeClear()
    {
        try { Console.Clear(); } catch { }
    }

    public static AudioSource Pick(List<AudioSource> sources)
    {
        if (sources.Count == 0)
            throw new CliError(ExitCodes.Source, "SOURCE", "未找到任何可用音源");

        var def = SourceResolver.Default(sources);
        if (Console.IsInputRedirected) return def;

        var selected = def.Index;
        while (true)
        {
            SafeClear();
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  asrgrab · 选择音源");
            Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  ↑↓ 移动    Enter 确认    Esc 用默认    数字键直选");
            Console.ResetColor();
            Console.WriteLine();

            for (var i = 0; i < sources.Count; i++)
            {
                var s = sources[i];
                var on = i == selected;
                if (on)
                {
                    Console.BackgroundColor = ConsoleColor.DarkCyan;
                    Console.ForegroundColor = ConsoleColor.White;
                }

                var icon = s.Kind == "loopback" ? "●" : "○";
                var kind = s.Kind == "loopback" ? "系统声音" : "输入设备";
                var mark = s.IsDefault ? "  默认" : "";
                Console.WriteLine($"  {(on ? ">" : " ")} [{s.Index}] {icon} {kind,-6} {s.Name}{mark}");

                if (on) Console.ResetColor();
            }

            Console.WriteLine();
            var key = Console.ReadKey(true);
            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    selected = (selected - 1 + sources.Count) % sources.Count;
                    break;
                case ConsoleKey.DownArrow:
                    selected = (selected + 1) % sources.Count;
                    break;
                case ConsoleKey.Enter:
                    Console.ResetColor();
                    SafeClear();
                    return sources[selected];
                case ConsoleKey.Escape:
                    Console.ResetColor();
                    SafeClear();
                    return def;
                default:
                    if (char.IsDigit(key.KeyChar))
                    {
                        var n = key.KeyChar - '0';
                        var hit = sources.FirstOrDefault(s => s.Index == n);
                        if (hit != null)
                        {
                            Console.ResetColor();
                            SafeClear();
                            return hit;
                        }
                    }
                    break;
            }
        }
    }
}

public static class LiveConsole
{
    public static void ClearLine()
    {
        try
        {
            var w = 60;
            try { w = Math.Max(20, Console.WindowWidth - 1); } catch { }
            Console.Error.Write("\r" + new string(' ', w) + "\r");
        }
        catch { }
    }

    public static void Tick(double elapsed, double db, long bytes, int segments)
    {
        ClearLine();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Error.Write($"  {Meter(db)} {db,6:0.0} dBFS  {elapsed,7:0.0}s  {segments,3} 句  {bytes / 1024.0 / 1024.0:0.0} MB");
        Console.ResetColor();
    }

    public static string Meter(double db)
    {
        const string levels = "▁▂▃▄▅▆▇█";
        const int n = 10;
        var lit = (int)Math.Clamp((db + 60.0) / 60.0 * n, 0, n);
        var sb = new StringBuilder(n);
        for (var i = 0; i < n; i++)
            sb.Append(i < lit ? levels[Math.Min(i, levels.Length - 1)] : '▁');
        return sb.ToString();
    }
}

public static class PcmUtil
{
    public static float[] ToMonoFloat(byte[] buffer, int bytes, WaveFormat fmt)
    {
        var ch = Math.Max(1, fmt.Channels);
        float[] interleaved;

        if ((fmt.Encoding == WaveFormatEncoding.IeeeFloat || fmt.Encoding == WaveFormatEncoding.Extensible)
            && fmt.BitsPerSample == 32)
        {
            var n = bytes / 4;
            interleaved = new float[n];
            Buffer.BlockCopy(buffer, 0, interleaved, 0, n * 4);
        }
        else if (fmt.BitsPerSample == 16)
        {
            var n = bytes / 2;
            interleaved = new float[n];
            for (var i = 0; i < n; i++) interleaved[i] = BitConverter.ToInt16(buffer, i * 2) / 32768f;
        }
        else if (fmt.BitsPerSample == 24)
        {
            var n = bytes / 3;
            interleaved = new float[n];
            for (var i = 0; i < n; i++)
            {
                var v = buffer[i * 3] | (buffer[i * 3 + 1] << 8) | (buffer[i * 3 + 2] << 16);
                if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
                interleaved[i] = v / 8388608f;
            }
        }
        else
        {
            throw new NotSupportedException($"不支持的音频格式: {fmt.Encoding} / {fmt.BitsPerSample}bit");
        }

        var frames = interleaved.Length / ch;
        var mono = new float[frames];
        if (ch == 1)
        {
            Array.Copy(interleaved, mono, frames);
        }
        else
        {
            for (var i = 0; i < frames; i++)
            {
                float sum = 0;
                for (var c = 0; c < ch; c++) sum += interleaved[i * ch + c];
                mono[i] = sum / ch;
            }
        }

        return mono;
    }

    public static float[] Resample(float[] input, int srcRate, int dstRate)
    {
        if (srcRate == dstRate || input.Length == 0) return input;
        var n = (int)Math.Round(input.Length * (double)dstRate / srcRate);
        if (n <= 0) return Array.Empty<float>();

        var output = new float[n];
        var step = (double)srcRate / dstRate;
        for (var i = 0; i < n; i++)
        {
            var pos = i * step;
            var idx = (int)pos;
            var frac = pos - idx;
            var a = input[Math.Min(idx, input.Length - 1)];
            var b = input[Math.Min(idx + 1, input.Length - 1)];
            output[i] = a + (b - a) * (float)frac;
        }

        return output;
    }

    public static byte[] ToPcm16Bytes(float[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var v = (short)Math.Clamp((int)(samples[i] * 32767f), -32768, 32767);
            bytes[i * 2] = (byte)(v & 0xFF);
            bytes[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }

        return bytes;
    }

    public static double Dbfs(float[] samples)
    {
        if (samples.Length == 0) return -120;
        double sum = 0;
        foreach (var s in samples) sum += s * s;
        var rms = Math.Sqrt(sum / samples.Length);
        if (rms <= 1e-9) return -120;
        return Math.Round(20 * Math.Log10(rms), 1);
    }
}

public sealed class WasapiRecorder : IDisposable
{
    private readonly AudioSource _source;
    private readonly int _targetRate;
    private readonly Action<float[]> _onFrame;
    private readonly Action<Exception> _onError;

    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private WasapiCapture? _capture;
    private WaveFormat? _format;

    public WasapiRecorder(AudioSource source, int targetRate, Action<float[]> onFrame, Action<Exception> onError)
    {
        _source = source;
        _targetRate = targetRate;
        _onFrame = onFrame;
        _onError = onError;
    }

    public void Start()
    {
        try
        {
            _enumerator = new MMDeviceEnumerator();
            _device = _enumerator.GetDevice(_source.DeviceId);
            _capture = _source.Kind == "loopback"
                ? new WasapiLoopbackCapture(_device)
                : new WasapiCapture(_device);

            _format = _capture.WaveFormat;
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _capture.StartRecording();
        }
        catch (Exception e)
        {
            throw new CliError(ExitCodes.Audio, "AUDIO", $"打开音源失败: {e.Message}", new()
            {
                ["source"] = _source.Public(),
            });
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            if (_format == null || e.BytesRecorded <= 0) return;
            var mono = PcmUtil.ToMonoFloat(e.Buffer, e.BytesRecorded, _format);
            var resampled = PcmUtil.Resample(mono, _format.SampleRate, _targetRate);
            if (resampled.Length > 0) _onFrame(resampled);
        }
        catch (Exception ex)
        {
            _onError(ex);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null) _onError(e.Exception);
    }

    public void Stop()
    {
        try { _capture?.StopRecording(); } catch { }
    }

    public void Dispose()
    {
        try
        {
            if (_capture != null)
            {
                _capture.DataAvailable -= OnDataAvailable;
                _capture.RecordingStopped -= OnRecordingStopped;
                _capture.Dispose();
            }
        }
        catch { }

        try { _device?.Dispose(); } catch { }
        try { _enumerator?.Dispose(); } catch { }
    }
}

public static class AudioDecoder
{
    public static bool HasFfmpeg()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, "ffmpeg.exe")))
                    return true;
            }
            catch { }
        }

        return false;
    }

    public static float[] DecodeTo16kMono(string path, string tempDir)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var naudioExts = new[] { ".wav", ".mp3", ".aiff", ".aif", ".wma" };

        if (naudioExts.Contains(ext))
        {
            try { return ReadWithNaudio(path); }
            catch { /* fall through to ffmpeg */ }
        }

        if (!HasFfmpeg())
        {
            throw new CliError(ExitCodes.Dependency, "DEPENDENCY",
                $"解码 {ext} 需要 ffmpeg（未找到）。安装: winget install ffmpeg，或先转成 16k 单声道 wav。",
                new() { ["dependency"] = "ffmpeg", ["input"] = path });
        }

        Directory.CreateDirectory(tempDir);
        var wav = Path.Combine(tempDir, Path.GetFileNameWithoutExtension(path) + "_16k.wav");
        RunFfmpeg(path, wav);
        return ReadWithNaudio(wav);
    }

    private static float[] ReadWithNaudio(string path)
    {
        using var reader = new AudioFileReader(path);
        ISampleProvider sp = reader;

        if (reader.WaveFormat.Channels == 2)
        {
            sp = new StereoToMonoSampleProvider(sp) { LeftVolume = 0.5f, RightVolume = 0.5f };
        }
        else if (reader.WaveFormat.Channels > 2)
        {
            throw new NotSupportedException($"声道数 {reader.WaveFormat.Channels} 暂不支持");
        }

        if (reader.WaveFormat.SampleRate != 16000)
        {
            sp = new WdlResamplingSampleProvider(sp, 16000);
        }

        var list = new List<float>();
        var buffer = new float[8192];
        int n;
        while ((n = sp.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < n; i++) list.Add(buffer[i]);
        }

        return list.ToArray();
    }

    private static void RunFfmpeg(string input, string output)
    {
        var psi = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(input);
        psi.ArgumentList.Add("-vn");
        psi.ArgumentList.Add("-ac");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-ar");
        psi.ArgumentList.Add("16000");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("wav");
        psi.ArgumentList.Add(output);

        using var p = Process.Start(psi) ?? throw new CliError(ExitCodes.Engine, "ENGINE", "无法启动 ffmpeg");
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new CliError(ExitCodes.Engine, "ENGINE", "ffmpeg 抽音轨失败: " + err.Trim(), new()
            {
                ["input"] = input,
            });
        }
    }
}
