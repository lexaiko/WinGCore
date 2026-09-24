using System.Diagnostics;

namespace GManager.Helpers;

/// <summary>
/// Opens Google web services in the default system browser with the specified account pre-selected (?authuser={email}).
/// </summary>
public static class BrowserLauncher
{
    public static void OpenService(string? email, string service = "myaccount")
    {
        var authUserParam = !string.IsNullOrWhiteSpace(email) ? $"authuser={Uri.EscapeDataString(email)}" : "";

        var url = service switch
        {
            "gmail" => string.IsNullOrEmpty(authUserParam)
                ? "https://mail.google.com/mail/u/"
                : $"https://mail.google.com/mail/u/?{authUserParam}",

            "drive" => string.IsNullOrEmpty(authUserParam)
                ? "https://drive.google.com/"
                : $"https://drive.google.com/?{authUserParam}",

            "calendar" => string.IsNullOrEmpty(authUserParam)
                ? "https://calendar.google.com/"
                : $"https://calendar.google.com/?{authUserParam}",

            "myaccount" => string.IsNullOrEmpty(authUserParam)
                ? "https://myaccount.google.com/"
                : $"https://myaccount.google.com/?{authUserParam}",

            _ => "https://myaccount.google.com/"
        };

        OpenUrl(url);
    }

    public static void OpenMail(string? email, string? threadId = null)
    {
        if (!string.IsNullOrWhiteSpace(threadId) && !string.IsNullOrWhiteSpace(email))
        {
            OpenUrl($"https://mail.google.com/mail/u/?authuser={Uri.EscapeDataString(email)}#inbox/{threadId}");
        }
        else
        {
            OpenService(email, "gmail");
        }
    }

    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore system browser launch errors
        }
    }
}
