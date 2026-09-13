using Microsoft.Win32;

namespace SimpleRemote.Platform;

/// <summary>
/// Start-with-Windows via the per-user Run key.
///
/// HKCU rather than HKLM, and no scheduled task: this needs no elevation, which keeps the whole
/// app installer-free and non-admin.
/// </summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SimpleRemote";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string existing && existing.Contains(ExecutablePath, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return false;
            }
        }
    }

    public static bool TrySet(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null) return false;

            if (enabled)
                key.SetValue(ValueName, $"\"{ExecutablePath}\" --minimized");
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);

            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// The real .exe, not the managed dll. Environment.ProcessPath is correct for both a normal
    /// build and a single-file publish, where Assembly.Location returns an empty string.
    /// </summary>
    public static string ExecutablePath =>
        Environment.ProcessPath ?? AppContext.BaseDirectory;
}
