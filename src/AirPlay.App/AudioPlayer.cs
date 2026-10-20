using AirPlay.Core.Audio;
using AirPlay.Core.Timing;
using AirPlay.Media;

namespace AirPlay.App;

/// <summary>
/// Plays one AirPlay audio stream (mirroring sound or music): frames arrive in order
/// (AudioStream reorders them), are decoded and sent to the speakers with a short buffer,
/// trimmed if it grows (sender clock slightly faster than the sound card).
/// </summary>
public sealed class AudioPlayer : IAudioSink
{
    private readonly AudioDecoder _decoder;
    private readonly PcmOutput _output;
    private readonly PlaybackClock? _clock;
    private readonly Action<string> _log;
    private long _frames;

    public AudioPlayer(AudioFormat format, PlaybackClock? clock, Action<string> log)
    {
        _log = log;
        _clock = clock;
        _decoder = new AudioDecoder(format.CompressionType);
        _output = new PcmOutput(clock, startAfter: TimeSpan.FromMilliseconds(100), maxBuffered: TimeSpan.FromMilliseconds(400),
            capacity: TimeSpan.FromSeconds(2), log);
        _log($"     音訊播放: {format.CodecName} → 預設喇叭");
    }

    public void OnAudioFrame(ReadOnlySpan<byte> frame, uint rtpTimestamp)
    {
        var pcm = _decoder.Decode(frame);
        if (pcm.IsEmpty)
        {
            if (_decoder.Errors is 1 or 100) _log($"     ⚠ 音訊解碼失敗 {_decoder.Errors} 次");
            return;
        }
        _frames++;
        _output.Add(pcm, _clock?.AudioRtpToRemoteNanos(rtpTimestamp));
    }

    public void SetVolume(double db) => _output.SetVolume(db);

    public void Flush() => _output.Flush();

    public void Dispose()
    {
        _output.Dispose();
        _decoder.Dispose();
        _log($"     音訊播放結束: {_frames} 幀, 解碼錯誤 {_decoder.Errors}, 追趕 {_output.Trims} 次");
    }
}
