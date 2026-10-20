using System.Diagnostics;
using System.Threading.Channels;
using AirPlay.Core;
using AirPlay.Core.Mirroring;
using AirPlay.Core.Timing;
using AirPlay.Media;

namespace AirPlay.App;

/// <summary>
/// Sink for one mirroring session: decodes H.264 on its own thread and puts each picture,
/// tagged with its presentation time, into the <see cref="FrameQueue"/> that the window shows from.
/// Decoding never skips frames (H.264 frames depend on earlier ones); display picks the
/// newest picture that is due, so it follows the audio when there is audio.
/// </summary>
public sealed class VideoPresenter : IMirrorSink
{
    private readonly FrameQueue _output;
    private readonly Action<string> _log;
    private readonly Action<bool> _onActiveChanged;
    private readonly Channel<VideoFrame> _queue = Channel.CreateUnbounded<VideoFrame>(new() { SingleReader = true });
    private readonly Task _decodeLoop;
    private readonly bool _useTimestamps;

    /// <param name="clock">The session clock, or null to show pictures as soon as they are decoded.</param>
    public VideoPresenter(FrameQueue output, PlaybackClock? clock, Action<string> log, Action<bool> onActiveChanged)
    {
        _output = output;
        Clock = clock;
        _useTimestamps = clock is not null;
        _log = log;
        _onActiveChanged = onActiveChanged;
        _decodeLoop = Task.Factory.StartNew(DecodeLoop, TaskCreationOptions.LongRunning);
    }

    public PlaybackClock? Clock { get; }

    public void OnFormat(VideoFormat format) { }

    public void OnFrame(VideoFrame frame) => _queue.Writer.TryWrite(frame);

    private void DecodeLoop()
    {
        using var decoder = new H264Decoder();
        var first = true;
        var clock = Stopwatch.StartNew();
        long shownAtLastReport = 0, decoded = 0;
        try
        {
            while (_queue.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (_queue.Reader.TryRead(out var frame))
                {
                    long? remoteNs = _useTimestamps ? PlaybackClock.VideoToRemoteNanos(frame.Timestamp) : null;
                    decoder.Decode(frame.AnnexB, picture =>
                    {
                        _output.Enqueue(picture, remoteNs);
                        decoded++;
                        if (first)
                        {
                            first = false;
                            _log($"     第一個畫面已解碼 {picture.Width}x{picture.Height}");
                            _onActiveChanged(true);
                        }
                    });
                }
                if (clock.Elapsed.TotalSeconds >= 10)
                {
                    _log($"     解碼: {(decoded - shownAtLastReport) / clock.Elapsed.TotalSeconds:F1} fps, 錯誤 {decoder.Errors}");
                    shownAtLastReport = decoded;
                    clock.Restart();
                }
            }
        }
        catch (Exception ex)
        {
            _log($"     解碼執行緒錯誤: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _decodeLoop.Wait(TimeSpan.FromSeconds(2));
        _onActiveChanged(false);
    }
}
