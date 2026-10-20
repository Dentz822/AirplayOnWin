using AirPlay.Core.Audio;
using AirPlay.Media;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AirPlay.App;

/// <summary>
/// Plays one AirPlay audio stream on the default Windows output device.
/// Frames arrive in order (AudioStream reorders them) and are decoded straight into a small
/// PCM buffer that WASAPI drains. The buffer is kept short so audio stays close to the video;
/// if it grows (sender clock slightly faster than the sound card), it is trimmed.
/// </summary>
public sealed class AudioPlayer : IAudioSink
{
    private static readonly TimeSpan TargetLatency = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MaxLatency = TimeSpan.FromMilliseconds(400);

    private readonly AudioDecoder _decoder;
    private readonly BufferedWaveProvider _buffer;
    private readonly WasapiOut _output;
    private readonly Action<string> _log;
    private bool _playing;
    private long _frames, _trims;

    public AudioPlayer(AudioFormat format, Action<string> log)
    {
        _log = log;
        _decoder = new AudioDecoder(format.CompressionType);
        _buffer = new BufferedWaveProvider(new WaveFormat(AudioDecoder.SampleRate, 16, AudioDecoder.Channels))
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true,
            ReadFully = true, // play silence while waiting instead of stopping
        };
        _output = new WasapiOut(AudioClientShareMode.Shared, useEventSync: true, latency: 40);
        _output.Init(_buffer);
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

        if (_buffer.BufferedDuration > MaxLatency)
        {
            _buffer.ClearBuffer(); // fell behind: jump back to live
            _trims++;
        }
        _buffer.AddSamples(pcm.ToArray(), 0, pcm.Length);

        // Start after a short pre-buffer so network jitter doesn't cause gaps.
        if (!_playing && _buffer.BufferedDuration >= TargetLatency)
        {
            _output.Play();
            _playing = true;
            _log("     開始播放聲音");
        }
    }

    /// <summary>AirPlay volume: 0 dB = full, -30 dB = quietest, -144 = mute.</summary>
    public void SetVolume(double db)
    {
        var gain = db <= -144 ? 0f : (float)Math.Clamp(Math.Pow(10, db / 20), 0, 1);
        try { _output.Volume = gain; }
        catch (Exception ex) { _log($"     無法設定音量: {ex.Message}"); }
    }

    public void Flush() => _buffer.ClearBuffer();

    public void Dispose()
    {
        _output.Stop();
        _output.Dispose();
        _decoder.Dispose();
        _log($"     音訊播放結束: {_frames} 幀, 解碼錯誤 {_decoder.Errors}, 追趕 {_trims} 次");
    }
}
