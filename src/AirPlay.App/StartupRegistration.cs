using Microsoft.Win32;

namespace AirPlay.App;

/// <summary>"Start with Windows" via HKCU\Software\Microsoft\Windows\CurrentVersion\Run (no admin needed).</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WindowsAirplay";
    public const string MinimizedArgument = "--minimized";

    private static string Command => $"\"{Environment.ProcessPath}\" {MinimizedArgument}";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && value == Command;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
