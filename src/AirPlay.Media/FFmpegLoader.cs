using FFmpeg.AutoGen;

namespace AirPlay.Media;

/// <summary>Points FFmpeg.AutoGen at the FFmpeg DLLs shipped by the Sdcb.FFmpeg.runtime package.</summary>
public static class FFmpegLoader
{
    private static readonly object Lock = new();
    private static bool _loaded;

    /// <summary>Loads FFmpeg once and returns its version string (e.g. "7.1").</summary>
    public static string EnsureLoaded()
    {
        lock (Lock)
        {
            if (!_loaded)
            {
                var baseDir = AppContext.BaseDirectory;
                // Single-file publish extracts native DLLs to a temp folder; the host lists it here.
                var nativeDirs = (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string ?? "")
                    .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
                var candidates = new[]
                {
                    Path.Combine(baseDir, "runtimes", "win-x64", "native"), // dotnet build / run
                    baseDir,                                                 // publish -r win-x64
                }.Concat(nativeDirs).ToArray();
                var dir = candidates.FirstOrDefault(d => File.Exists(Path.Combine(d, "avcodec-61.dll")))
                    ?? throw new FileNotFoundException("找不到 FFmpeg DLL（avcodec-61.dll）", candidates[0]);
                ffmpeg.RootPath = dir;
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
                _loaded = true;
            }
            return ffmpeg.av_version_info();
        }
    }

    internal static string ErrorText(int error)
    {
        unsafe
        {
            const int size = 256;
            var buffer = stackalloc byte[size];
            ffmpeg.av_strerror(error, buffer, size);
            return System.Runtime.InteropServices.Marshal.PtrToStringAnsi((IntPtr)buffer) ?? $"error {error}";
        }
    }
}
