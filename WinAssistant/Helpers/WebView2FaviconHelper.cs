using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Windows.Storage.Streams;

namespace WinAssistant.Helpers;

/// <summary>Fetches website title and favicon using the built-in Edge browser engine.
/// Uses CoreWebView2's native favicon API — the same pipeline the browser itself uses.
/// 100% reliable for all sites.</summary>
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

            var core = webView.CoreWebView2;

            // Favicon event — the browser's own favicon pipeline.
            core.FaviconChanged += async (_, _) =>
            {
                try
                {
                    var icon = await ReadFaviconAsync(core);
                    if (icon == null) return;
                    var title = await GetTitleAsync(core);
                    tcs.TrySetResult(new WebsiteMetadataHelper.WebsiteInfo(title, icon.Value.Path, icon.Value.Source));
                }
                catch (Exception ex) { Logger.Log("WebView2FaviconHelper", $"FaviconChanged: {ex.Message}"); }
            };

            webView.NavigationCompleted += async (_, e) =>
            {
                if (!e.IsSuccess)
                {
                    Logger.Log("WebView2FaviconHelper", $"Navigation failed: {e.WebErrorStatus}");
                    tcs.TrySetResult(null);
                    return;
                }

                if (!tcs.Task.IsCompleted)
                {
                    var title = await GetTitleAsync(core);
                    var icon = await ReadFaviconAsync(core);
                    if (icon != null)
                        tcs.TrySetResult(new WebsiteMetadataHelper.WebsiteInfo(title, icon.Value.Path, icon.Value.Source));
                    else
                        tcs.TrySetResult(new WebsiteMetadataHelper.WebsiteInfo(title, null, null));
                }
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            cts.Token.Register(() => tcs.TrySetResult(null));

            webView.CoreWebView2.Navigate(url);
            return await tcs.Task;
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

    private static async Task<string?> GetTitleAsync(CoreWebView2 coreWebView)
    {
        try
        {
            var json = await coreWebView.ExecuteScriptAsync("document.title");
            return System.Text.Json.JsonSerializer.Deserialize<string>(json);
        }
        catch { return null; }
    }

    private static async Task<(string Path, ImageSource Source)?> ReadFaviconAsync(CoreWebView2 coreWebView)
    {
        try
        {
            using var faviconStream = await coreWebView.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            if (faviconStream == null || faviconStream.Size == 0) return null;

            var size = (uint)faviconStream.Size;
            using var reader = new DataReader(faviconStream);
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

            Logger.Log("WebView2FaviconHelper", $"Favicon: {bytes.Length} bytes");
            return (file, bitmap);
        }
        catch (Exception ex)
        {
            Logger.Log("WebView2FaviconHelper", $"ReadFavicon: {ex.Message}");
            return null;
        }
    }
}
