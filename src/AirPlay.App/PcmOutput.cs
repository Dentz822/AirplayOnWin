using AirPlay.Core.Timing;
using AirPlay.Media;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AirPlay.App;

/// <summary>
/// 44.1 kHz 16-bit stereo PCM to the default Windows output device (WASAPI), used by both
/// mirroring audio and AirPlay video. Each chunk comes with its presentation time; after adding
/// it, the clock is told which moment is audible right now so video can follow the audio.
/// </summary>
public sealed class PcmOutput : IDisposable
{
    /// <summary>WASAPI buffer (40 ms) plus device/resampler delay: time from leaving our buffer to the speaker.</summary>
    private const long OutputLatencyNs = 50_000_000;

    private readonly BufferedWaveProvider _buffer;
    private readonly WasapiOut _output;
    private readonly PlaybackClock? _clock;
    private readonly TimeSpan _startAfter;
    private readonly TimeSpan? _maxBuffered;
    private readonly Action<string> _log;
    private bool _playing;
    private bool _paused;

    public long Trims { get; private set; }

    /// <param name="startAfter">Pre-buffer before playback starts.</param>
    /// <param name="maxBuffered">Live streams: drop the buffer if it grows past this (null = never).</param>
    public PcmOutput(PlaybackClock? clock, TimeSpan startAfter, TimeSpan? maxBuffered, TimeSpan capacity, Action<string> log)
    {
        _clock = clock;
        _startAfter = startAfter;
        _maxBuffered = maxBuffered;
        _log = log;
        _buffer = new BufferedWaveProvider(new WaveFormat(AudioDecoder.SampleRate, 16, AudioDecoder.Channels))
        {
            BufferDuration = capacity,
            DiscardOnBufferOverflow = true,
            ReadFully = true, // play silence while waiting instead of stopping
        };
        _output = new WasapiOut(AudioClientShareMode.Shared, useEventSync: true, latency: 40);
        _output.Init(_buffer);
    }

    public TimeSpan Buffered => _buffer.BufferedDuration;

    /// <param name="startNs">Presentation time of the first sample, or null if unknown.</param>
    public void Add(ReadOnlySpan<byte> pcm, long? startNs)
    {
        if (pcm.IsEmpty) return;
        if (_maxBuffered is { } max && _buffer.BufferedDuration > max)
        {
            _buffer.ClearBuffer(); // fell behind a live stream: jump back to live
            Trims++;
        }
        _buffer.AddSamples(pcm.ToArray(), 0, pcm.Length);

        if (!_playing && !_paused && _buffer.BufferedDuration >= _startAfter)
        {
            _output.Play();
            _playing = true;
            _log("     開始播放聲音");
        }

        // This chunk ends `buffered + output latency` from now, so the speaker is currently
        // playing (chunk end − that delay).
        if (_playing && startNs is long start)
        {
            var chunkNs = pcm.Length / (2 * AudioDecoder.Channels) * 1_000_000_000L / AudioDecoder.SampleRate;
            var aheadNs = (long)(_buffer.BufferedDuration.TotalMilliseconds * 1_000_000) + OutputLatencyNs;
            _clock?.ReportAudible(start + chunkNs - aheadNs);
        }
    }

    public void Pause()
    {
        _paused = true;
        if (_playing) _output.Pause();
        _clock?.SetPaused(true);
    }

    public void Resume()
    {
        _paused = false;
        _clock?.SetPaused(false);
        if (_playing) _output.Play();
    }

    /// <summary>Drops queued audio (pause/seek on the sender). Playback restarts after pre-buffering.</summary>
    public void Flush()
    {
        _buffer.ClearBuffer();
        if (_playing)
        {
            _output.Stop();
            _playing = false;
        }
    }

    /// <summary>AirPlay volume: 0 dB = full, -30 dB = quietest, -144 = mute.</summary>
    public void SetVolume(double db)
    {
        var gain = db <= -144 ? 0f : (float)Math.Clamp(Math.Pow(10, db / 20), 0, 1);
        try { _output.Volume = gain; }
        catch (Exception ex) { _log($"     無法設定音量: {ex.Message}"); }
    }

    public void Dispose()
    {
        _output.Stop();
        _output.Dispose();
    }
}
