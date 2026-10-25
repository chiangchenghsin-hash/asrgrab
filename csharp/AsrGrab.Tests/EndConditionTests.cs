using AsrGrab;
using Xunit;

public class EndConditionTests
{
    private static EndCondition.VideoSample S(double ct = 1, double dur = 10, bool ended = false, string? src = "a.mp4")
        => new(ct, dur, ended, src);

    [Fact]
    public void EndedWins()
    {
        var (stop, reason) = EndCondition.Evaluate(S(ended: true, src: "b.mp4"), "a.mp4", 999, 1);
        Assert.True(stop);
        Assert.Equal(EndReason.Ended, reason);
    }

    [Fact]
    public void ClipSwitchOnSrcChange()
    {
        var (stop, reason) = EndCondition.Evaluate(S(src: "b.mp4"), "a.mp4", 5, 0);
        Assert.True(stop);
        Assert.Equal(EndReason.ClipSwitch, reason);
    }

    [Fact]
    public void SameSrcContinues()
    {
        var (stop, reason) = EndCondition.Evaluate(S(), "a.mp4", 5, 0);
        Assert.False(stop);
        Assert.Null(reason);
    }

    [Fact]
    public void MaxSecondsFuse()
    {
        var (stop, reason) = EndCondition.Evaluate(S(), "a.mp4", 60.1, 60);
        Assert.True(stop);
        Assert.Equal(EndReason.MaxSeconds, reason);
    }

    [Fact]
    public void NullSrcNeverClipSwitches()
    {
        var (stop, _) = EndCondition.Evaluate(S(src: null), null, 5, 0);
        Assert.False(stop);
    }

    [Fact]
    public void EmptySrcIsTreatedAsAbsent()
    {
        // CDP 探测缺 src 时给 ""，不是 null——空串视为缺席，不触发 clip-switch
        var (stop1, _) = EndCondition.Evaluate(S(src: ""), "a.mp4", 5, 0);
        Assert.False(stop1);
        var (stop2, _) = EndCondition.Evaluate(S(src: "a.mp4"), "", 5, 0);
        Assert.False(stop2);
        var (stop3, reason3) = EndCondition.Evaluate(S(src: "b.mp4"), "a.mp4", 5, 0);
        Assert.True(stop3);
        Assert.Equal(EndReason.ClipSwitch, reason3);
    }

    [Fact]
    public void NullSrcMidRunDoesNotClipSwitch()
    {
        var (stop, _) = EndCondition.Evaluate(S(src: null), "a.mp4", 5, 0);
        Assert.False(stop);
    }

    [Theory]
    [InlineData(60.0, 60, true)]
    [InlineData(59.9, 60, false)]
    public void FuseBoundary(double elapsed, int max, bool expectStop)
    {
        var (stop, reason) = EndCondition.Evaluate(S(), "a.mp4", elapsed, max);
        Assert.Equal(expectStop, stop);
        if (expectStop) Assert.Equal(EndReason.MaxSeconds, reason);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void NonFiniteElapsedTripsFuse(double elapsed)
    {
        var (stop, reason) = EndCondition.Evaluate(S(), "a.mp4", elapsed, 60);
        Assert.True(stop);
        Assert.Equal(EndReason.MaxSeconds, reason);
    }
}
