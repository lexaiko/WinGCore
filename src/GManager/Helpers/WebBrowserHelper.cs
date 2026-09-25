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
                <meta http-equiv="X-UA-Compatible" content="IE=edge" />
                <meta name="viewport" content="width=device-width, initial-scale=1.0" />
                <style>
                  html, body {
                    overflow-x: hidden !important;
                    margin: 0;
                    padding: 20px 24px;
                    -ms-overflow-style: -ms-autohiding-scrollbar;
                  }
                  body {
                    font-family: -apple-system, BlinkMacSystemFont, "SF Pro Text", "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
                    font-size: 13.5px;
                    line-height: 1.6;
                    color: #1d1d1f;
                    background: transparent;
                    word-wrap: break-word;
                    word-break: break-word;
                  }
                  @media (prefers-color-scheme: dark) {
                    body { color: #e5e5ea; }
                    a { color: #0a84ff; }
                  }
                  img { max-width: 100% !important; height: auto; }
                  table { max-width: 100% !important; }
                  td, th { word-wrap: break-word; word-break: break-word; }
                  pre, code { white-space: pre-wrap; font-family: "SF Mono", Consolas, monospace; font-size: 12px; }
                  * { box-sizing: border-box; }
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
