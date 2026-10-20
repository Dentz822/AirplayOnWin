using System.IO;
using System.Text.Json;

namespace AirPlay.App;

/// <summary>User settings, saved as %LOCALAPPDATA%\WindowsAirplay\settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>Closing the window hides it to the tray (the receiver keeps running).</summary>
    public bool CloseToTray { get; set; }
    public bool AlwaysOnTop { get; set; }
    /// <summary>Frame rate the iPhone is asked for: 30 or 60.</summary>
    public int MaxFps { get; set; } = 60;
    /// <summary>
    /// App AirPlay button plays videos here (YouTube, Safari, Twitch). iOS then also switches
    /// screen mirroring to video mode when such a video starts. Off = always mirror; app AirPlay
    /// buttons give sound only.
    /// </summary>
    public bool AppVideo { get; set; } = true;
    /// <summary>Phones may listen to the PC's sound (web page on the AirPlay port).</summary>
    public bool ShareAudio { get; set; }
    /// <summary>Random part of the listening address, so it can't be guessed.</summary>
    public string? ShareToken { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsAirplay", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception) { /* corrupt file: fall back to defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
