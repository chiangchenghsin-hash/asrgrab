using AsrGrab;
using Xunit;

public class GrabEndTests
{
    [Fact]
    public void BuildsEndedEvent()
    {
        var ev = GrabEnd.Build(EndReason.Ended, 0, "s1", "transcripts/transcript_s1.md", 12.34, 7, null);
        Assert.Equal("grab_end", ev["event"]);
        Assert.Equal("ended", ev["end_reason"]);
        Assert.Equal(0, ev["exit_code"]);
        Assert.Equal("s1", ev["session_id"]);
        Assert.Equal("transcripts/transcript_s1.md", ev["out_file"]);
        Assert.Equal(12.3, ev["elapsed_s"]);
        Assert.Equal(7, ev["segments"]);
        Assert.Null(ev["error"]);
    }

    [Theory]
    [InlineData(EndReason.ClipSwitch, "clip-switch")]
    [InlineData(EndReason.MaxSeconds, "max-seconds")]
    [InlineData(EndReason.Error, "error")]
    public void MapsReasonStrings(EndReason r, string want)
    {
        var ev = GrabEnd.Build(r, 3, "s", null, 0, 0, "boom");
        Assert.Equal(want, ev["end_reason"]);
        Assert.Equal("boom", ev["error"]);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void NonFiniteElapsedFallsBackToZero(double elapsed)
    {
        var ev = GrabEnd.Build(EndReason.Ended, 0, "s", null, elapsed, 0, null);
        Assert.Equal(0.0, ev["elapsed_s"]);
    }
}
