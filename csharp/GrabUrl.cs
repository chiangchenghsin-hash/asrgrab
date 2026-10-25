namespace AsrGrab;

public enum EndReason { Ended, ClipSwitch, MaxSeconds, Error }

public sealed class GrabUrlOptions
{
    public string Url = "";
    public int MaxSeconds;
    public int ChunkSec = 12;
    public string Model = "small";
    public bool NoDiarize;
    public int NumSpeakers;
    public int VideoTimeoutSec = 30;
    public string? NotifyCmd;
    public bool KeepBrowser;
    public int Port = 9222;
    public string? ModelsDir;

    public static GrabUrlOptions Parse(string[] args)
    {
        var ap = new ArgParser(args);
        var o = new GrabUrlOptions();
        if (ap.Positional.Count < 1 || string.IsNullOrWhiteSpace(ap.Positional[0]))
            throw new CliError(ExitCodes.Usage, "USAGE", "grab_url 需要 <url>", new()
            {
                ["hint"] = "asrgrab grab_url <url> [--max-seconds N] [--notify-cmd \"<cmd>\"]",
            });
        o.Url = ap.Positional[0].Trim();
        if (!o.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !o.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new CliError(ExitCodes.Usage, "USAGE", "url 必须以 http(s):// 开头", new() { ["url"] = o.Url });
        o.MaxSeconds = Math.Max(0, ap.Int("max-seconds", 0));
        o.ChunkSec = Math.Max(1, ap.Int("chunk", 12));
        o.Model = ap.Str("model", "small");
        o.NoDiarize = ap.Flag("no-diarize");
        o.NumSpeakers = Math.Max(0, ap.Int("num-speakers", 0));
        o.VideoTimeoutSec = Math.Max(3, ap.Int("video-timeout", 30));
        var notify = ap.Get("notify-cmd");
        o.NotifyCmd = string.IsNullOrEmpty(notify) ? null : notify;
        o.KeepBrowser = ap.Flag("keep-browser");
        o.Port = ap.Int("port", 9222);
        o.ModelsDir = ap.Get("models-dir");
        return o;
    }
}

public static class GrabEnd
{
    public static Dictionary<string, object?> Build(
        EndReason reason, int exitCode, string sessionId, string? outFile,
        double elapsedSec, int segments, string? error)
        => new()
        {
            ["event"] = "grab_end",
            ["end_reason"] = reason switch
            {
                EndReason.Ended => "ended",
                EndReason.ClipSwitch => "clip-switch",
                EndReason.MaxSeconds => "max-seconds",
                _ => "error",
            },
            ["exit_code"] = exitCode,
            ["session_id"] = sessionId,
            ["out_file"] = outFile,
            ["elapsed_s"] = double.IsFinite(elapsedSec) ? Math.Round(elapsedSec, 1) : 0.0,
            ["segments"] = segments,
            ["error"] = error,
        };
}

public static class EndCondition
{
    public sealed record VideoSample(double CurrentTimeSec, double DurationSec, bool Ended, string? Src);

    /// 返回 (是否停止, 原因)。优先级：ended > clip-switch > max-seconds（spec §3）。
    /// elapsedSec 为 grab_url 全程墙钟（对应 grab_end.elapsed_s）；VideoSample 的 CurrentTimeSec/DurationSec 是视频内位置，判定不使用。
    public static (bool Stop, EndReason? Reason) Evaluate(
        VideoSample s, string? firstSrc, double elapsedSec, int maxSeconds)
    {
        if (s.Ended) return (true, EndReason.Ended);
        if (!string.IsNullOrEmpty(firstSrc) && !string.IsNullOrEmpty(s.Src) && s.Src != firstSrc)
            return (true, EndReason.ClipSwitch);
        if (maxSeconds > 0 && (!double.IsFinite(elapsedSec) || elapsedSec >= maxSeconds))
            return (true, EndReason.MaxSeconds);
        return (false, null);
    }
}
