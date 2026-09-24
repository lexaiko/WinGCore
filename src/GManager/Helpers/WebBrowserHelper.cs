using System.Windows;
using System.Windows.Controls;

namespace GManager.Helpers;

public static class WebBrowserHelper
{
    public static readonly DependencyProperty HtmlProperty =
        DependencyProperty.RegisterAttached("Html", typeof(string), typeof(WebBrowserHelper),
            new PropertyMetadata(null, OnHtmlChanged));

    public static string? GetHtml(DependencyObject obj) => (string?)obj.GetValue(HtmlProperty);
    public static void SetHtml(DependencyObject obj, string? value) => obj.SetValue(HtmlProperty, value);

    private static void OnHtmlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WebBrowser wb)
        {
            var html = e.NewValue as string;
            if (string.IsNullOrWhiteSpace(html))
            {
                try { wb.Navigate("about:blank"); } catch { }
            }
            else
            {
                var styledHtml = $$"""
                <!DOCTYPE html>
                <html>
                <head>
                <meta charset="utf-8" />
                <style>
                  body {
                    font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;
                    font-size: 14px;
                    line-height: 1.6;
                    margin: 16px;
                    color: #202124;
                    background: #ffffff;
                    word-wrap: break-word;
                  }
                  @media (prefers-color-scheme: dark) {
                    body { background: #1e1e1e; color: #e1e1e1; }
                    a { color: #8ab4f8; }
                  }
                  img { max-width: 100%; height: auto; }
                  table { max-width: 100%; }
                  pre, code { white-space: pre-wrap; font-family: Consolas, monospace; }
                </style>
                </head>
                <body>
                {{html}}
                </body>
                </html>
                """;
                try { wb.NavigateToString(styledHtml); } catch { }
            }
        }
    }
}
