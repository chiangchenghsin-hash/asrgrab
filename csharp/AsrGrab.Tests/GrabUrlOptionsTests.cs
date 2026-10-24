using AsrGrab;
using Xunit;

public class GrabUrlOptionsTests
{
    [Fact]
    public void ParsesMinimalUrl()
    {
        var o = GrabUrlOptions.Parse(new[] { "https://example.com/v" });
        Assert.Equal("https://example.com/v", o.Url);
        Assert.Equal(30, o.VideoTimeoutSec);
        Assert.Equal(12, o.ChunkSec);
        Assert.Equal("small", o.Model);
        Assert.False(o.KeepBrowser);
        Assert.Null(o.NotifyCmd);
        Assert.Equal(0, o.MaxSeconds);
        Assert.Equal(9222, o.Port);
    }

    [Fact]
    public void MissingUrlThrowsUsage()
    {
        var e = Assert.Throws<CliError>(() => GrabUrlOptions.Parse(System.Array.Empty<string>()));
        Assert.Equal(ExitCodes.Usage, e.ExitCode);
    }

    [Fact]
    public void RejectsNonHttpUrl()
    {
        var e = Assert.Throws<CliError>(() => GrabUrlOptions.Parse(new[] { "ftp://x" }));
        Assert.Equal(ExitCodes.Usage, e.ExitCode);
    }

    [Fact]
    public void ParsesFlags()
    {
        var o = GrabUrlOptions.Parse(new[]
        {
            "https://e.com", "--max-seconds", "60", "--chunk", "8", "--model", "full",
            "--no-diarize", "--num-speakers", "2", "--video-timeout", "90",
            "--notify-cmd", "echo done", "--keep-browser", "--port", "9333",
        });
        Assert.Equal(60, o.MaxSeconds);
        Assert.Equal(8, o.ChunkSec);
        Assert.Equal("full", o.Model);
        Assert.True(o.NoDiarize);
        Assert.Equal(2, o.NumSpeakers);
        Assert.Equal(90, o.VideoTimeoutSec);
        Assert.Equal("echo done", o.NotifyCmd);
        Assert.True(o.KeepBrowser);
        Assert.Equal(9333, o.Port);
    }

    [Fact]
    public void ClampsTimeoutAndChunkToPositive()
    {
        var o = GrabUrlOptions.Parse(new[] { "https://e.com", "--video-timeout", "1", "--chunk", "0" });
        Assert.Equal(3, o.VideoTimeoutSec);   // 下限 3s
        Assert.Equal(1, o.ChunkSec);          // 下限 1s
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void WhitespaceUrlThrowsUsage(string bad)
    {
        var e = Assert.Throws<CliError>(() => GrabUrlOptions.Parse(new[] { bad }));
        Assert.Equal(ExitCodes.Usage, e.ExitCode);
    }

    [Fact]
    public void TrimsPaddedUrl()
    {
        var o = GrabUrlOptions.Parse(new[] { "  https://e.com  " });
        Assert.Equal("https://e.com", o.Url);
    }

    [Fact]
    public void PaddedBadUrlFailsSchemeCheckWithTrimmedValue()
    {
        var e = Assert.Throws<CliError>(() => GrabUrlOptions.Parse(new[] { "  ftp://x  " }));
        Assert.Equal(ExitCodes.Usage, e.ExitCode);
        Assert.Equal("ftp://x", e.Extra["url"]);
    }

    [Fact]
    public void NegativeMaxSecondsAndNumSpeakersClampToZero()
    {
        var o = GrabUrlOptions.Parse(new[] { "https://e.com", "--max-seconds", "-5", "--num-speakers", "-3" });
        Assert.Equal(0, o.MaxSeconds);
        Assert.Equal(0, o.NumSpeakers);
    }

    [Fact]
    public void ParsesModelsDir()
    {
        var o = GrabUrlOptions.Parse(new[] { "https://e.com", "--models-dir", "D:\\m" });
        Assert.Equal("D:\\m", o.ModelsDir);
        Assert.Null(GrabUrlOptions.Parse(new[] { "https://e.com" }).ModelsDir);
    }
}
