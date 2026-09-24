using Microsoft.Win32;

namespace GManager.Core;

/// <summary>
/// Registers and removes the "gmanager://" custom URL protocol handler in the Windows Registry
/// (HKEY_CURRENT_USER) so that the OS routes OAuth redirect callbacks back into GManager.
///
/// This mirrors how iOS registers per-app URL schemes in Info.plist.
/// The HKCU location requires no elevation and is per-user.
/// </summary>
public static class RegistryProtocolHelper
{
    private const string ProtocolScheme = AppConfig.OAuthRedirectScheme; // "gmanager"
    private const string RegistryRoot   = $@"Software\Classes\{ProtocolScheme}";

    /// <summary>
    /// Registers gmanager:// → GManager.exe "%1" in HKCU.
    /// Safe to call on every startup; idempotent.
    /// </summary>
    public static void Register()
    {
        try
        {
            var exePath = Environment.ProcessPath
                          ?? System.Reflection.Assembly.GetExecutingAssembly().Location;

            using var key = Registry.CurrentUser.CreateSubKey(RegistryRoot, writable: true);
            key.SetValue(string.Empty,  "URL:GManager OAuth Callback");
            key.SetValue("URL Protocol", string.Empty);

            using var iconKey = key.CreateSubKey("DefaultIcon");
            iconKey.SetValue(string.Empty, $"\"{exePath}\",0");

            using var cmdKey = key.CreateSubKey(@"shell\open\command");
            cmdKey.SetValue(string.Empty, $"\"{exePath}\" \"%1\"");
        }
        catch
        {
            // Non-fatal — fall back to loopback server if registry write fails
        }
    }

    /// <summary>Removes the HKCU registration on uninstall.</summary>
    public static void Unregister()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(RegistryRoot, throwOnMissingSubKey: false);
        }
        catch { }
    }

    /// <summary>
    /// Returns true if the first command-line argument looks like a gmanager:// URI
    /// (i.e., this process was launched by Windows as a protocol handler).
    /// </summary>
    public static bool IsProtocolActivation(string[] args, out string uri)
    {
        uri = string.Empty;
        if (args.Length == 0) return false;

        var arg = args[0];
        if (arg.StartsWith($"{ProtocolScheme}:", StringComparison.OrdinalIgnoreCase))
        {
            uri = arg;
            return true;
        }
        return false;
    }
}
