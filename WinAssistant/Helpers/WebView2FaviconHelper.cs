using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Windows.Storage.Streams;

namespace WinAssistant.Helpers;

/// <summary>Fetches website title + favicon using Edge's browser engine (WebView2).
///
/// Strategy (simplified):
/// 1. Load the page in a hidden WebView2, wait for NavigationCompleted
/// 2. Wait 2s extra for SPA sites (where JS sets the favicon after mount)
/// 3. Primary: CoreWebView2.GetFaviconAsync() — Edge's internal favicon DB
/// 4. Fallback: execute JS to find &lt;link rel="icon"&gt; → inject &lt;img&gt; → capture response
///    via WebResourceResponseReceived (uses Edge's network stack, not HttpClient)
///
/// Unlike HttpClient, WebView2 uses Edge's full network stack (TLS, cookies, auth, CDN),
/// so it works even when direct HTTP is blocked. This is the best approach for SPA sites
/// like e.bilibili.com where the favicon is set dynamically by JavaScript.</summary>
public static class WebView2FaviconHelper
{
    /// <summary>Fetch favicon + title using WebView2. Returns title even without icon.</summary>
    public static async Task<WebsiteMetadataHelper.WebsiteInfo?> FetchAsync(string url, XamlRoot xamlRoot)
    {
        var (faviconPath, faviconSource, title) = await FetchFaviconWithWebView2Async(url, xamlRoot);

        // Return whatever we have — title alone is still useful
        return new WebsiteMetadataHelper.WebsiteInfo(title, faviconPath, faviconSource);
    }

    private static async Task<(string? Path, ImageSource? Source, string? Title)> FetchFaviconWithWebView2Async(
        string url, XamlRoot xamlRoot)
    {
        WebView2? wv = null;
        Grid? host = null;
        bool disposed = false;
        string? pageTitle = null;

        try
        {
            wv = new WebView2
            {
                Width = 1,
                Height = 1,
                Opacity = 0,
                IsHitTestVisible = false
            };
            host = new Grid
            {
                Width = 1,
                Height = 1,
                Opacity = 0,
                Visibility = Visibility.Visible, // Must be visible for WebView2 to render
                Children = { wv }
            };
            if (xamlRoot.Content is Panel p) p.Children.Add(host);

            await wv.EnsureCoreWebView2Async();
            wv.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;
            wv.CoreWebView2.Settings.AreDevToolsEnabled = false;
            wv.CoreWebView2.Settings.IsStatusBarEnabled = false;

            // ── Navigate and wait for completion ──
            var navTcs = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs?>();
            using var navCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            navCts.Token.Register(() => navTcs.TrySetResult(null));

            void OnNavCompleted(object? _, CoreWebView2NavigationCompletedEventArgs e)
            {
                navTcs.TrySetResult(e);
            }

            wv.NavigationCompleted += OnNavCompleted;

            try
            {
                wv.CoreWebView2.Navigate(url);
                var navResult = await navTcs.Task;

                if (navResult == null || !navResult.IsSuccess)
                {
                    Logger.Log("WebView2FaviconHelper", "Navigation failed or timeout");
                    return (null, null, null);
                }
            }
            finally
            {
                wv.NavigationCompleted -= OnNavCompleted;
            }

            // ── Wait for SPA sites (JS needs time to mount & set favicon) ──
            await Task.Delay(2000);

            // Read page title from WebView2 (already parsed by browser)
            pageTitle = wv.CoreWebView2.DocumentTitle;
            if (!string.IsNullOrWhiteSpace(pageTitle))
                Logger.Log("WebView2FaviconHelper", $"Page title: {pageTitle}");

            // ── Primary: JS scan DOM for icon links ──
            var jsIcons = await ScanForIconsAsync(wv);
            
            // If nothing found after 2s, try again after 4s more (for slow SPAs like e.bilibili.com)
            if (jsIcons == null || jsIcons.Count == 0)
            {
                await Task.Delay(4000);
                jsIcons = await ScanForIconsAsync(wv);
            }

            if (jsIcons != null)
            {
                foreach (var icon in jsIcons)
                {
                    if (string.IsNullOrEmpty(icon.href)) continue;
                    if (icon.href.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

                    var (path, source) = await DownloadIconViaWebView2Async(wv, icon.href);
                    if (path != null)
                    {
                        Logger.Log("WebView2FaviconHelper", $"JS: {icon.rel} {icon.href} -> {path}");
                        return (path, source, pageTitle);
                    }
                }
            }

            // ── Fallback: Edge's internal favicon DB (small cached version) ──
            try
            {
                var faviconStream = await wv.CoreWebView2.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
                if (faviconStream != null && faviconStream.Size > 200)
                {
                    var bytes = await ReadStreamAsync(faviconStream);
                    Logger.Log("WebView2FaviconHelper", $"GetFaviconAsync fallback: {bytes.Length}B");
                    var (path, source) = await SaveIconBytes(bytes);
                    if (path != null) return (path, source, pageTitle);
                }
            }
            catch (Exception ex)
            {
                Logger.Log("WebView2FaviconHelper", $"GetFaviconAsync fallback error: {ex.Message}");
            }

            return (null, null, pageTitle);
        }
        catch (Exception ex)
        {
            Logger.Log("WebView2FaviconHelper", $"Error: {ex.Message}");
            return (null, null, pageTitle);
        }
        finally
        {
            if (!disposed)
            {
                disposed = true;
                try
                {
                    if (wv != null)
                    {
                        wv.Close();
                        if (wv.Parent is Panel wp) wp.Children.Remove(wv);
                    }
                    if (host != null && host.Parent is Panel hp) hp.Children.Remove(host);
                }
                catch { }
            }
        }
    }

    private static async Task<(string? Path, ImageSource? Source)> SaveIconBytes(byte[] bytes)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinAssistant", "website-icons");
        Directory.CreateDirectory(dir);
        var hash = Convert.ToHexString(SHA256.HashData(bytes))[..16];
        var file = Path.Combine(dir, $"{hash}.png");
        await File.WriteAllBytesAsync(file, bytes);

        var bmp = new BitmapImage();
        bmp.UriSource = new Uri(file);
        return (file, bmp);
    }

    private static async Task<byte[]> ReadStreamAsync(IRandomAccessStream s)
    {
        var sz = (uint)s.Size;
        using var r = new DataReader(s);
        await r.LoadAsync(sz);
        var b = new byte[sz];
        r.ReadBytes(b);
        return b;
    }

    /// <summary>Download icon bytes through WebView2's own network stack.
    ///
    /// Injects a hidden &lt;img&gt; element and intercepts the response via
    /// WebResourceResponseReceived. Unlike HttpClient, this goes through
    /// Edge's full network stack (TLS, CDN, cookies, auth), so it works
    /// even when direct HTTP is blocked (e.g. Chinese CDNs).</summary>
    private static async Task<(string? Path, ImageSource? Source)> DownloadIconViaWebView2Async(
        WebView2 wv, string iconUrl)
    {
        var tcs = new TaskCompletionSource<byte[]?>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var reg = cts.Token.Register(() => tcs.TrySetResult(null));

        void OnResponse(object? s, CoreWebView2WebResourceResponseReceivedEventArgs e)
        {
            if (tcs.Task.IsCompleted) return;
            if (e.Response.StatusCode != 200) return;
            if (!string.Equals(e.Request.Uri, iconUrl, StringComparison.OrdinalIgnoreCase)) return;

            _ = ReadResponseContentAsync(e.Response, tcs);
        }

        wv.CoreWebView2.WebResourceResponseReceived += OnResponse;
        try
        {
            // Inject hidden <img> to trigger download through WebView2's network stack
            var escaped = iconUrl.Replace("\\", "\\\\").Replace("'", "\\'");
            await wv.CoreWebView2.ExecuteScriptAsync(
                "(function(){var i=new Image();i.src='" + escaped + "';" +
                "i.style.position='absolute';i.style.left='-9999px';" +
                "document.body.appendChild(i);})()");

            var bytes = await tcs.Task;
            if (bytes?.Length > 200)
                return await SaveIconBytes(bytes);
        }
        finally
        {
            wv.CoreWebView2.WebResourceResponseReceived -= OnResponse;
        }

        return (null, null);
    }

    /// <summary>Read raw bytes from a WebResourceResponseReceived response body.</summary>
    private static async Task ReadResponseContentAsync(
        CoreWebView2WebResourceResponseView response, TaskCompletionSource<byte[]?> tcs)
    {
        try
        {
            using var stream = await response.GetContentAsync();
            stream.Seek(0);
            using var r = new DataReader(stream);
            await r.LoadAsync((uint)stream.Size);
            var b = new byte[stream.Size];
            r.ReadBytes(b);
            tcs.TrySetResult(b);
        }
        catch
        {
            tcs.TrySetResult(null);
        }
    }

    /// <summary>Execute JS to scan DOM for favicon links.</summary>
    private static async Task<List<IconLink>?> ScanForIconsAsync(WebView2 wv)
    {
        try
        {
            var json = await wv.CoreWebView2.ExecuteScriptAsync("""
                (function(){
                    var links=document.querySelectorAll('link[rel*="icon"],link[rel*="apple-touch"],link[rel="fluid-icon"],link[rel="mask-icon"]');
                    var out=[];
                    links.forEach(function(l){
                        var s=l.getAttribute('sizes')||'';
                        var m=s.match(/(\d+)/);
                        out.push({href:l.href,size:m?parseInt(m[1]):0,rel:l.getAttribute('rel')});
                    });
                    out.sort(function(a,b){return b.size-a.size;});
                    return out;
                })()
                """);
            return JsonSerializer.Deserialize<List<IconLink>>(json);
        }
        catch (Exception ex)
        {
            Logger.Log("WebView2FaviconHelper", $"ScanForIcons error: {ex.Message}");
            return null;
        }
    }

    private record IconLink(string href, int size, string? rel);
}
