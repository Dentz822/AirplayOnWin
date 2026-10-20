using System.Text;

namespace AirPlay.Core;

/// <summary>
/// Thread-safe timestamped log file, UTF-8 with BOM so Notepad / Get-Content / editors
/// never mistake it for Big5. Optionally echoes each line somewhere else (e.g. the console).
/// </summary>
public sealed class FileLog : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly Action<string>? _echo;
    private readonly object _lock = new();

    public string Path { get; }

    public FileLog(string directory, string prefix, Action<string>? echo = null)
    {
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        _writer = new StreamWriter(Path, false, new UTF8Encoding(true)) { AutoFlush = true };
        _echo = echo;
    }

    /// <summary>%LOCALAPPDATA%\WindowsAirplay\logs</summary>
    public static string DefaultDirectory => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsAirplay", "logs");

    public void Write(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        lock (_lock)
        {
            _echo?.Invoke(line);
            try { _writer.WriteLine(line); } catch (ObjectDisposedException) { }
        }
    }

    public void Dispose()
    {
        lock (_lock) _writer.Dispose();
    }
}
