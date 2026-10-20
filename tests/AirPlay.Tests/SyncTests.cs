using AirPlay.Core.Timing;
using AirPlay.Media;

namespace AirPlay.Tests;

public class PlaybackClockTests
{
    private const ulong Offset1900 = 2_208_988_800UL;

    [Fact]
    public void NtpToNanosHandlesTimesBeyond2Pow31Seconds()
    {
        // iPhone NTP time = uptime + 1900→1970 offset: about 2.2e9 s, which overflows signed Q32.32.
        var q32 = ((Offset1900 + 3600) << 32) | 0x80000000UL; // +0.5 s
        Assert.Equal((long)(Offset1900 + 3600) * 1_000_000_000L + 500_000_000L, PlaybackClock.NtpToNanos(q32));
    }

    [Fact]
    public void VideoAndAudioLandOnTheSameTimeline()
    {
        var clock = new PlaybackClock();
        // Audio: RTP 88200 (2 s of samples) plays at uptime 102 s (NTP includes the 1900 offset).
        clock.UpdateAudioSync(88200, (Offset1900 + 102) << 32);
        // Video frame at uptime 102.5 s (video timestamps lack the 1900 offset).
        var videoNs = PlaybackClock.VideoToRemoteNanos(((ulong)102 << 32) | 0x80000000UL);
        // The audio sample heard together with it: 0.5 s after the sync point.
        var audioNs = clock.AudioRtpToRemoteNanos(88200 + 22050)!.Value;
        Assert.Equal(videoNs, audioNs);
    }

    [Fact]
    public void RtpWrapAroundStaysContinuous()
    {
        var clock = new PlaybackClock();
        clock.UpdateAudioSync(uint.MaxValue - 99, (Offset1900 + 10) << 32);
        var before = clock.AudioRtpToRemoteNanos(uint.MaxValue - 99)!.Value;
        var after = clock.AudioRtpToRemoteNanos(341)!.Value; // 441 samples later, past the wrap
        Assert.Equal(10_000_000L, after - before);
    }

    [Fact]
    public void AudibleTimeAdvancesThenGoesStale()
    {
        var clock = new PlaybackClock();
        Assert.Null(clock.AudibleRemoteNanosNow());
        clock.ReportAudible(5_000_000_000);
        var a = clock.AudibleRemoteNanosNow()!.Value;
        Thread.Sleep(50);
        var b = clock.AudibleRemoteNanosNow()!.Value;
        Assert.InRange(b - a, 40_000_000, 200_000_000);
        clock.ResetAudio();
        Assert.Null(clock.AudibleRemoteNanosNow());
    }
}

public class FrameQueueTests
{
    private static BgraFrame Picture(byte marker) => new(2, 1, 8, [marker, 0, 0, 0, marker, 0, 0, 0]);

    [Fact]
    public void ShowsEverythingImmediatelyWithoutAudio()
    {
        var q = new FrameQueue();
        q.Enqueue(Picture(1), 100);
        q.Enqueue(Picture(2), 200);
        var f = q.TakeDue(null);
        Assert.Equal(2, f!.Pixels[0]); // newest due picture; the older one is skipped
        Assert.Equal(0, q.Count);
    }

    [Fact]
    public void HoldsPicturesUntilTheAudioReachesThem()
    {
        var q = new FrameQueue();
        q.Enqueue(Picture(1), 1_000);
        q.Enqueue(Picture(2), 2_000);
        q.Enqueue(Picture(3), 3_000);

        Assert.Null(q.TakeDue(500));             // audio hasn't reached the first picture
        Assert.Equal(1, q.TakeDue(1_500)!.Pixels[0]);
        Assert.Equal(3, q.TakeDue(3_000)!.Pixels[0]); // 2 and 3 both due: show 3
        Assert.Null(q.TakeDue(9_000));
    }

    [Fact]
    public void ShowsAPictureAnywayAfterWaitingTooLong()
    {
        var q = new FrameQueue();
        q.Enqueue(Picture(7), long.MaxValue); // timestamp far in the future (bad mapping)
        Assert.Null(q.TakeDue(0));
        Thread.Sleep(FrameQueue.MaxHold + TimeSpan.FromMilliseconds(100));
        Assert.Equal(7, q.TakeDue(0)!.Pixels[0]);
        Assert.Equal(1, q.ForcedByTimeout);
    }

    [Fact]
    public void BoundsTheQueue()
    {
        var q = new FrameQueue();
        for (var i = 0; i < FrameQueue.MaxQueued + 10; i++) q.Enqueue(Picture((byte)i), long.MaxValue);
        Assert.Equal(FrameQueue.MaxQueued, q.Count);
        Assert.Equal(10, q.Dropped);
    }
}
