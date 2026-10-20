using System.Diagnostics;
using System.Threading.Channels;
using AirPlay.Core;
using AirPlay.Core.Mirroring;
using AirPlay.Media;

namespace AirPlay.App;

/// <summary>
/// Latest decoded picture, shared between the decode thread (writer) and the UI thread (reader).
/// Only the newest picture matters for display, so the writer simply overwrites it.
/// </summary>
public sealed class LatestFrame
{
    private readonly object _lock = new();
    private byte[] _pixels = [];

    public int Width { get; private set; }
    public int Height { get; private set; }
    public long Version { get; private set; }

    public void Store(BgraFrame f)
    {
        lock (_lock)
        {
            var size = f.Stride * f.Height;
            if (_pixels.Length != size) _pixels = new byte[size];
            Buffer.BlockCopy(f.Pixels, 0, _pixels, 0, size);
            Width = f.Width;
            Height = f.Height;
            Version++;
        }
    }

    /// <summary>Runs <paramref name="read"/> with the pixels while holding the lock.</summary>
    public void Read(Action<int, int, byte[]> read)
    {
        lock (_lock) read(Width, Height, _pixels);
    }
}

/// <summary>
/// Sink for one mirroring session: decodes H.264 on its own thread and publishes pictures to
/// <see cref="LatestFrame"/>. Decoding never skips frames (H.264 frames depend on earlier ones);
/// display just shows whichever picture is newest.
/// </summary>
public sealed class VideoPresenter : IMirrorSink
{
    private readonly LatestFrame _output;
    private readonly Action<string> _log;
    private readonly Action<bool> _onActiveChanged;
    private readonly Channel<VideoFrame> _queue = Channel.CreateUnbounded<VideoFrame>(new() { SingleReader = true });
    private readonly Task _decodeLoop;

    public VideoPresenter(LatestFrame output, Action<string> log, Action<bool> onActiveChanged)
    {
        _output = output;
        _log = log;
        _onActiveChanged = onActiveChanged;
        _decodeLoop = Task.Factory.StartNew(DecodeLoop, TaskCreationOptions.LongRunning);
    }

    public void OnFormat(VideoFormat format) { }

    public void OnFrame(VideoFrame frame) => _queue.Writer.TryWrite(frame);

    private void DecodeLoop()
    {
        using var decoder = new H264Decoder();
        var first = true;
        var clock = Stopwatch.StartNew();
        long shownAtLastReport = 0, shown = 0;
        try
        {
            while (_queue.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (_queue.Reader.TryRead(out var frame))
                {
                    decoder.Decode(frame.AnnexB, picture =>
                    {
                        _output.Store(picture);
                        shown++;
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
                    _log($"     解碼: {(shown - shownAtLastReport) / clock.Elapsed.TotalSeconds:F1} fps, 錯誤 {decoder.Errors}");
                    shownAtLastReport = shown;
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
