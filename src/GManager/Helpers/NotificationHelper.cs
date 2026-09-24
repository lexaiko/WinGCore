using System.IO;
using GManager.Models;
using Microsoft.Toolkit.Uwp.Notifications;

namespace GManager.Helpers;

/// <summary>
/// Helper for dispatching native interactive Windows Toast Notifications with deep linking.
/// </summary>
public static class NotificationHelper
{
    public static void ShowNewMailToast(GoogleAccount account, MailMessage message)
    {
        try
        {
            var builder = new ToastContentBuilder()
                .AddHeader("gmanager_mail", "GManager Mail", "")
                .AddText(string.IsNullOrEmpty(message.SenderName) ? message.SenderEmail : message.SenderName)
                .AddText(string.IsNullOrEmpty(message.Subject) ? "(No Subject)" : message.Subject)
                .AddText(message.Snippet)
                .AddArgument("action", "view_message")
                .AddArgument("accountId", account.Id)
                .AddArgument("messageId", message.Id);

            // Add avatar if locally cached
            if (!string.IsNullOrEmpty(account.AvatarLocalPath) && File.Exists(account.AvatarLocalPath))
            {
                builder.AddAppLogoOverride(new Uri(account.AvatarLocalPath), ToastGenericAppLogoCrop.Circle);
            }

            // Quick interactive actions
            builder.AddButton(new ToastButton()
                .SetContent("Open in Browser")
                .AddArgument("action", "open_browser")
                .AddArgument("email", account.Email)
                .AddArgument("threadId", message.ThreadId));

            builder.Show();
        }
        catch
        {
            // Toast notification subsystem might be disabled by Windows Focus Assist or policy
        }
    }
}
