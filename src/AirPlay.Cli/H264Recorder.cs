using AirPlay.Core;
using AirPlay.Core.Mirroring;

/// <summary>
/// Saves one mirroring session as a raw H.264 (Annex-B) file. Writing starts at the first
/// key frame so the file is playable from the beginning (ffplay / VLC / mpv can open it).
/// </summary>
internal sealed class H264Recorder : IMirrorSink
{
    private readonly FileStream _file;
    private readonly Action<string> _log;
    private bool _started;

    public string Path { get; }

    public H264Recorder(string directory, Action<string> log)
    {
        _log = log;
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, $"mirror-{DateTime.Now:yyyyMMdd-HHmmss}.h264");
        _file = new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.Read);
        _log($"     錄影檔: {Path}");
    }

    public void OnFormat(VideoFormat format) { }

    public void OnFrame(VideoFrame frame)
    {
        if (!_started && !frame.IsKeyFrame) return;
        _started = true;
        _file.Write(frame.AnnexB);
    }

    public void Dispose()
    {
        var size = _file.Length;
        _file.Dispose();
        if (size == 0) File.Delete(Path);
        else _log($"     錄影已存檔: {Path} ({size / 1024.0 / 1024:F1} MB)");
    }
}
