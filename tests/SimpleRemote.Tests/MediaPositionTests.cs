using SimpleRemote.Media;
using Xunit;

namespace SimpleRemote.Tests;

public class MediaPositionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 20, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Duration = TimeSpan.FromMinutes(50);

    /// <summary>
    /// The reported bug: a browser publishes Position once when playback starts and then leaves it.
    /// Ten seconds later playback is ten seconds further on, not where the snapshot says.
    /// </summary>
    [Fact]
    public void PlayingAdvancesFromTheSnapshot()
    {
        var ms = MediaController.EstimatePositionMs(
            TimeSpan.FromSeconds(60), Duration, Now.AddSeconds(-10), Now, playing: true, rate: 1.0);

        Assert.Equal(70_000, ms);
    }

    [Fact]
    public void PausedReportsTheSnapshotAsIs()
    {
        var ms = MediaController.EstimatePositionMs(
            TimeSpan.FromSeconds(60), Duration, Now.AddSeconds(-10), Now, playing: false, rate: 1.0);

        Assert.Equal(60_000, ms);
    }

    [Fact]
    public void PlaybackRateScalesTheAdvance()
    {
        var ms = MediaController.EstimatePositionMs(
            TimeSpan.FromSeconds(60), Duration, Now.AddSeconds(-10), Now, playing: true, rate: 1.5);

        Assert.Equal(75_000, ms);
    }

    [Fact]
    public void UnsetLastUpdatedTimeIsNotExtrapolated()
    {
        var ms = MediaController.EstimatePositionMs(
            TimeSpan.FromSeconds(60), Duration, default, Now, playing: true, rate: 1.0);

        Assert.Equal(60_000, ms);
    }

    [Fact]
    public void EstimateNeverRunsPastTheEnd()
    {
        var ms = MediaController.EstimatePositionMs(
            Duration - TimeSpan.FromSeconds(5), Duration, Now.AddMinutes(-10), Now, playing: true, rate: 1.0);

        Assert.Equal((long)Duration.TotalMilliseconds, ms);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void NonsenseRateFallsBackToNormalSpeed(double rate)
    {
        var ms = MediaController.EstimatePositionMs(
            TimeSpan.FromSeconds(60), Duration, Now.AddSeconds(-10), Now, playing: true, rate: rate);

        Assert.Equal(70_000, ms);
    }

    [Fact]
    public void SnapshotStampedInTheFutureIsNotRewound()
    {
        var ms = MediaController.EstimatePositionMs(
            TimeSpan.FromSeconds(60), Duration, Now.AddSeconds(2), Now, playing: true, rate: 1.0);

        Assert.Equal(60_000, ms);
    }
}
