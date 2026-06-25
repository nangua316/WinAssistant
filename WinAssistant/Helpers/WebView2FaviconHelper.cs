using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Windows.Storage.Streams;

namespace WinAssistant.Helpers;

/// <summary>Fetches website title and favicon by intercepting the WebView2's own
/// network responses — the same way a real browser gets the favicon.
/// Edge's network stack handles all TLS / CDN / anti-bot issues properly.</summary>
public static class WebView2FaviconHelper
{
    public static async Task<WebsiteMetadataHelper.WebsiteInfo?> FetchAsync(string url, XamlRoot xamlRoot)
    {
        Logger.Log("WebView2FaviconHelper", $"FetchAsync: {url}");

        WebView2? webView = null;
        Grid? hostPanel = null;

        try
        {
            webView = new WebView2 { Width = 1, Height = 1, Opacity = 0, IsHitTestVisible = false };
            hostPanel = new Grid { Visibility = Visibility.Collapsed, Children = { webView } };

            if (xamlRoot.Content is Panel p) p.Children.Add(hostPanel);
            await webView.EnsureCoreWebView2Async();
            webView.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;
            webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            webView.CoreWebView2.Settings.IsZoomControlEnabled = false;

            var tcs = new TaskCompletionSource<WebsiteMetadataHelper.WebsiteInfo?>();
            string? jsFaviconUrl = null;

            // Intercept the browser's OWN favicon download — uses Edge's real network stack.
            webView.CoreWebView2.WebResourceResponseReceived += async (_, args) =>
            {
                try
                {
                    var uri = args.Request.Uri;
                    var contentType = args.Response.Headers.GetHeader("Content-Type") ?? "";
                    Logger.Log("WebView2FaviconHelper", $"Response: {uri} -> {contentType} ({args.Response.StatusCode})");

                    bool isFavicon = uri.Contains("favicon", StringComparison.OrdinalIgnoreCase)
                        || (contentType.Contains("icon", StringComparison.OrdinalIgnoreCase) && args.Response.StatusCode == 200);
                    if (!isFavicon) return;

                    using var stream = await args.Response.GetContentAsync();
                    if (stream == null || stream.Size == 0) return;

                    var size = (uint)stream.Size;
                    using var reader = new DataReader(stream);
                    await reader.LoadAsync(size);
                    var bytes = new byte[size];
                    reader.ReadBytes(bytes);

                    var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WinAssistant", "website-icons");
                    System.IO.Directory.CreateDirectory(tempDir);
                    var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16];
                    var file = System.IO.Path.Combine(tempDir, $"{hash}.png");
                    await System.IO.File.WriteAllBytesAsync(file, bytes);

                    var bitmap = new BitmapImage();
                    bitmap.DecodePixelWidth = 64;
                    bitmap.DecodePixelHeight = 64;
                    bitmap.UriSource = new Uri(file);

                    Logger.Log("WebView2FaviconHelper", $"Got favicon ({bytes.Length}B) from: {uri}");
                    tcs.TrySetResult(new WebsiteMetadataHelper.WebsiteInfo(null, file, bitmap));
                }
                catch (Exception ex) { Logger.Log("WebView2FaviconHelper", $"WebResource error: {ex.Message}"); }
            };

            webView.NavigationCompleted += async (_, e) =>
            {
                if (!e.IsSuccess)
                {
                    Logger.Log("WebView2FaviconHelper", $"Nav failed: {e.WebErrorStatus}");
                    tcs.TrySetResult(null);
                    return;
                }

                // Get the title.
                var titleJson = await webView.CoreWebView2.ExecuteScriptAsync("document.title");
                var title = System.Text.Json.JsonSerializer.Deserialize<string>(titleJson);

                // If favicon was already captured via WebResourceResponseReceived, we're done.
                if (tcs.Task.IsCompleted) return;

                // Try to get the declared favicon URL from the page, then navigate to it.
                var urlJson = await webView.CoreWebView2.ExecuteScriptAsync("""
                    (function() {
                        var s = ['link[rel*="apple-touch-icon"]','link[rel="fluid-icon"]','link[rel="shortcut icon"]','link[rel~="icon"]','link[rel="mask-icon"]'];
                        for (var i=0;i<s.length;i++){var e=document.querySelector(s[i]);if(e&&e.href)return e.href;}
                        var h=window.location.hostname.split('.');if(h.length>2){return window.location.protocol+'//www.'+h.slice(-2).join('.')+'/favicon.ico';}
                        return window.location.origin+'/favicon.ico';
                    })()
                    """);
                var faviconUrl = System.Text.Json.JsonSerializer.Deserialize<string>(urlJson);
                Logger.Log("WebView2FaviconHelper", $"JS favicon URL: {faviconUrl}, title={title}");

                if (!string.IsNullOrEmpty(faviconUrl))
                {
                    // Navigate to the favicon URL so Edge downloads it and our
                    // WebResourceResponseReceived handler captures the bytes.
                    webView.CoreWebView2.Navigate(faviconUrl);
                }
                else
                {
                    tcs.TrySetResult(new WebsiteMetadataHelper.WebsiteInfo(title, null, null));
                }
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            cts.Token.Register(() => tcs.TrySetResult(null));

            webView.CoreWebView2.Navigate(url);
            var result = await tcs.Task;

            // If WebResourceResponseReceived captured a favicon, it has title=null.
            // Fill in the title from NavigationCompleted.
            if (result != null && result.Title == null && result.FaviconSource != null)
            {
                // Wait briefly for title if needed.
                for (int i = 0; i < 10; i++)
                {
                    await Task.Delay(200);
                    var tJson = await webView.CoreWebView2.ExecuteScriptAsync("document.title");
                    var t = System.Text.Json.JsonSerializer.Deserialize<string>(tJson);
                    if (!string.IsNullOrEmpty(t))
                        return new WebsiteMetadataHelper.WebsiteInfo(t, result.FaviconPath, result.FaviconSource);
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            Logger.Log("WebView2FaviconHelper", $"Error: {ex.Message}");
            return null;
        }
        finally
        {
            try
            {
                webView?.Close();
                if (webView?.Parent is Panel panel) panel.Children.Remove(webView);
                if (hostPanel?.Parent is Panel hp) hp.Children.Remove(hostPanel);
            }
            catch { }
        }
    }
}
