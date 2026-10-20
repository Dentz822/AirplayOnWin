using System.Diagnostics;

namespace AirPlay.Core.Timing;

/// <summary>
/// A/V sync for one session, done in the iPhone's own clock ("remote time", nanoseconds):
///   • audio: 0x54 sync packets map an RTP timestamp to a remote NTP time; other samples are
///     placed relative to it at 44.1 kHz.
///   • video: each packet header carries remote NTP time *without* the 1900→1970 offset that
///     the NTP and audio timestamps include, so it is added back here.
/// The audio player reports which remote instant is audible right now; video frames are shown
/// once that instant reaches them ("video follows audio"). With no live audio, video is shown
/// as soon as it is decoded.
/// </summary>
public sealed class PlaybackClock
{
    public const int AudioSampleRate = 44100;
    private const long SecondsFrom1900To1970 = 2_208_988_800L;
    private static readonly long AudioStaleAfterTicks = Stopwatch.Frequency / 2; // 500 ms

    private readonly object _lock = new();
    private (uint Rtp, long RemoteNs)? _sync;
    private long _audibleRemoteNs;
    private long _audibleAtTicks;
    private bool _hasAudible;

    /// <summary>NTP Q32.32 → nanoseconds, without overflowing for times past 2^31 s.</summary>
    public static long NtpToNanos(ulong q32) =>
        (long)((q32 >> 32) * 1_000_000_000UL + (((q32 & 0xFFFFFFFFUL) * 1_000_000_000UL) >> 32));

    /// <summary>Video packet timestamp → remote time (adds the missing 1900→1970 offset).</summary>
    public static long VideoToRemoteNanos(ulong timestamp) =>
        NtpToNanos(timestamp) + SecondsFrom1900To1970 * 1_000_000_000L;

    /// <summary>Called for each 0x54 sync packet.</summary>
    public void UpdateAudioSync(uint rtp, ulong ntp)
    {
        lock (_lock) _sync = (rtp, NtpToNanos(ntp));
    }

    public bool HasAudioSync { get { lock (_lock) return _sync is not null; } }

    /// <summary>Remote time at which the sample with this RTP timestamp should be heard.</summary>
    public long? AudioRtpToRemoteNanos(uint rtp)
    {
        lock (_lock)
        {
            if (_sync is not { } s) return null;
            var samples = (int)(rtp - s.Rtp); // wrap-safe signed distance
            return s.RemoteNs + samples * 1_000_000_000L / AudioSampleRate;
        }
    }

    /// <summary>The audio player: "the sample for this remote time is coming out of the speaker now".</summary>
    public void ReportAudible(long remoteNs)
    {
        lock (_lock)
        {
            _audibleRemoteNs = remoteNs;
            _audibleAtTicks = Stopwatch.GetTimestamp();
            _hasAudible = true;
        }
    }

    /// <summary>
    /// The remote time being heard right now, extrapolated from the last report;
    /// null when there is no live audio (never started, or nothing reported for 500 ms).
    /// </summary>
    public long? AudibleRemoteNanosNow()
    {
        lock (_lock)
        {
            if (!_hasAudible) return null;
            var elapsed = Stopwatch.GetTimestamp() - _audibleAtTicks;
            if (elapsed > AudioStaleAfterTicks) return null;
            return _audibleRemoteNs + elapsed * 1_000_000_000L / Stopwatch.Frequency;
        }
    }

    /// <summary>Audio stopped or was flushed: video falls back to "show when decoded".</summary>
    public void ResetAudio()
    {
        lock (_lock)
        {
            _hasAudible = false;
            _sync = null;
        }
    }
}
