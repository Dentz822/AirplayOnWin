using System.Text;
using AirPlay.Core.Metadata;

namespace AirPlay.Tests;

public class NowPlayingTests
{
    private static byte[] Item(string tag, byte[] value) =>
        [.. Encoding.ASCII.GetBytes(tag), (byte)(value.Length >> 24), (byte)(value.Length >> 16), (byte)(value.Length >> 8), (byte)value.Length, .. value];

    [Fact]
    public void ParsesDmapTrackInfo()
    {
        var inner = Item("minm", Encoding.UTF8.GetBytes("夜曲"))
            .Concat(Item("asar", Encoding.UTF8.GetBytes("周杰倫")))
            .Concat(Item("caps", [1]))                  // other tags are ignored
            .Concat(Item("asal", Encoding.UTF8.GetBytes("十一月的蕭邦"))).ToArray();
        var tags = NowPlayingParser.ParseDmap(Item("mlit", inner));

        Assert.Equal("夜曲", tags["minm"]);
        Assert.Equal("周杰倫", tags["asar"]);
        Assert.Equal("十一月的蕭邦", tags["asal"]);
        Assert.False(tags.ContainsKey("caps"));
    }

    [Fact]
    public void IgnoresTruncatedDmap()
    {
        var data = Item("mlit", Item("minm", Encoding.UTF8.GetBytes("abc")));
        Assert.Empty(NowPlayingParser.ParseDmap(data.AsSpan(0, data.Length - 2)));
    }

    [Fact]
    public void ParsesProgressIncludingRtpWrapAround()
    {
        var p = NowPlayingParser.ParseProgress("progress: 1000/442000/8821000")!.Value;
        Assert.Equal(10.0, p.Position.TotalSeconds, 2);
        Assert.Equal(200.0, p.Duration.TotalSeconds, 2);

        // start just below 2^32, current/end after the wrap
        var w = NowPlayingParser.ParseProgress($"progress: {uint.MaxValue - 44099}/44100/{441000 - 44100}")!.Value;
        Assert.Equal(2.0, w.Position.TotalSeconds, 2);
        Assert.Equal(10.0, w.Duration.TotalSeconds, 2);

        Assert.Null(NowPlayingParser.ParseProgress("volume: -10"));
        Assert.Null(NowPlayingParser.ParseProgress("progress: 1/2"));
    }

    [Fact]
    public void PositionAdvancesOnlyWhilePlaying()
    {
        var np = new NowPlaying
        {
            Position = TimeSpan.FromSeconds(10),
            Duration = TimeSpan.FromSeconds(11),
            PositionAtTicks = System.Diagnostics.Stopwatch.GetTimestamp() - System.Diagnostics.Stopwatch.Frequency * 5,
        };
        Assert.Equal(TimeSpan.FromSeconds(10), np.CurrentPosition(playing: false));
        Assert.Equal(TimeSpan.FromSeconds(11), np.CurrentPosition(playing: true)); // capped at duration
    }
}
