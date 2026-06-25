using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace WinAssistant.Helpers;

/// <summary>Fallback favicon fetcher using a real browser engine (WebView2).
/// Useful for sites that block plain HTTP requests but work fine in a browser.
/// Must be called on the UI thread.</summary>
public static class WebView2FaviconHelper
{
    public static async Task<WebsiteMetadataHelper.WebsiteInfo?> FetchAsync(string url, XamlRoot xamlRoot)
    {
        WebView2? webView = null;
        Grid? hostPanel = null;

        try
        {
            // Create an off-screen WebView2 and attach it to the visual tree.
            webView = new WebView2
            {
                Width = 1,
                Height = 1,
                Opacity = 0,
                IsHitTestVisible = false
            };
            hostPanel = new Grid
            {
                Visibility = Microsoft.UI.Xaml.Visibility.Collapsed,
                Children = { webView }
            };

            if (xamlRoot.Content is Panel rootPanel)
            {
                rootPanel.Children.Add(hostPanel);
            }
            else if (xamlRoot.Content is FrameworkElement fe && fe.Parent is Panel parentPanel)
            {
                parentPanel.Children.Add(hostPanel);
            }
            else
            {
                Logger.Log("WebView2FaviconHelper", "Could not attach host panel to visual tree");
            }

            await webView.EnsureCoreWebView2Async();

            // Disable unnecessary features for a fast, headless fetch.
            webView.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;
            webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            webView.CoreWebView2.Settings.IsZoomControlEnabled = false;

            var tcs = new TaskCompletionSource<WebsiteMetadataHelper.WebsiteInfo?>();

            webView.NavigationCompleted += async (_, e) =>
            {
                try
                {
                    if (!e.IsSuccess)
                    {
                        Logger.Log("WebView2FaviconHelper", $"Navigation failed: {e.WebErrorStatus}");
                        tcs.TrySetResult(null);
                        return;
                    }

                    // Get title.
                    var titleJson = await webView.CoreWebView2.ExecuteScriptAsync("document.title");
                    var title = JsonSerializer.Deserialize<string>(titleJson);

                    // Get favicon URL via JavaScript (handles dynamically set icons too).
                    const string faviconScript = """
                        (function() {
                            var selectors = [
                                'link[rel*="apple-touch-icon"]',
                                'link[rel="fluid-icon"]',
                                'link[rel="shortcut icon"]',
                                'link[rel~="icon"]',
                                'link[rel="mask-icon"]'
                            ];
                            for (var i = 0; i < selectors.length; i++) {
                                var el = document.querySelector(selectors[i]);
                                if (el && el.href) return el.href;
                            }
                            // No icon declared — try root domain's favicon
                            // (subdomains like e.bilibili.com often serve no icon,
                            //  while the main domain www.bilibili.com has one).
                            var host = window.location.hostname;
                            var parts = host.split('.');
                            if (parts.length > 2) {
                                var root = parts.slice(parts.length - 2).join('.');
                                return window.location.protocol + '//www.' + root + '/favicon.ico';
                            }
                            return window.location.origin + '/favicon.ico';
                        })()
                        """;
                    var faviconJson = await webView.CoreWebView2.ExecuteScriptAsync(faviconScript);
                    var faviconUrl = JsonSerializer.Deserialize<string>(faviconJson);

                    Logger.Log("WebView2FaviconHelper", $"title={title}, faviconUrl={faviconUrl}");

                    if (string.IsNullOrEmpty(faviconUrl))
                    {
                        tcs.TrySetResult(new WebsiteMetadataHelper.WebsiteInfo(title, null, null));
                        return;
                    }

                    // Download the favicon.
                    var info = await WebsiteMetadataHelper.FetchAsync(faviconUrl);
                    tcs.TrySetResult(new WebsiteMetadataHelper.WebsiteInfo(
                        title,
                        info.FaviconPath,
                        info.FaviconSource));
                }
                catch (Exception ex)
                {
                    Logger.Log("WebView2FaviconHelper", $"NavigationCompleted error: {ex.Message}");
                    tcs.TrySetResult(null);
                }
            };

            // Timeout safety net.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            cts.Token.Register(() =>
            {
                Logger.Log("WebView2FaviconHelper", "Timeout");
                tcs.TrySetResult(null);
            });

            webView.CoreWebView2.Navigate(url);
            return await tcs.Task;
        }
        catch (Exception ex)
        {
            Logger.Log("WebView2FaviconHelper", $"Fetch error: {ex.Message}");
            return null;
        }
        finally
        {
            try
            {
                webView?.Close();
                if (webView?.Parent is Panel panel)
                    panel.Children.Remove(webView);
                if (hostPanel?.Parent is Panel hostParent)
                    hostParent.Children.Remove(hostPanel);
            }
            catch { }
        }
    }
}
