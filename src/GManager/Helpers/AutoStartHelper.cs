using System.Diagnostics;
using Microsoft.Win32;

namespace GManager.Helpers;

/// <summary>
/// Manages Windows login auto-startup via HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
/// </summary>
public static class AutoStartHelper
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "GManager";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            return key?.GetValue(ValueName) != null;
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            if (key == null) return;

            if (enable)
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                {
                    key.SetValue(ValueName, $"\"{exePath}\" --minimized");
                }
            }
            else
            {
                key.DeleteValue(ValueName, false);
            }
        }
        catch
        {
            // Ignore registry permissions failure
        }
    }
}
