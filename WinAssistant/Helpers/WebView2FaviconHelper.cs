using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using System.Text.Json;
using Windows.Storage.Streams;

namespace WinAssistant.Helpers;

/// <summary>Fetches website title + favicon using Edge's own network stack.
/// Two-phase approach: (1) load page -> get title + find favicon URL,
/// (2) load favicon URL -> intercept raw response bytes via WebResourceResponseReceived.
/// Edge handles all TLS / CDN / anti-bot issues that HttpClient can't.</summary>
public static class WebView2FaviconHelper
{
    public static async Task<WebsiteMetadataHelper.WebsiteInfo?> FetchAsync(string url, XamlRoot xamlRoot)
    {
        WebView2? wv = null;
        Grid? host = null;
        try
        {
            wv = new WebView2 { Width = 1, Height = 1, Opacity = 0, IsHitTestVisible = false };
            host = new Grid { Visibility = Visibility.Collapsed, Children = { wv } };
            if (xamlRoot.Content is Panel p) p.Children.Add(host);
            await wv.EnsureCoreWebView2Async();
            wv.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;
            wv.CoreWebView2.Settings.AreDevToolsEnabled = false;
            wv.CoreWebView2.Settings.IsStatusBarEnabled = false;

            var tcs = new TaskCompletionSource<WebsiteMetadataHelper.WebsiteInfo?>();
            string? pageTitle = null;
            string? targetFaviconUrl = null;

            // Phase 2: capture the favicon bytes when Edge downloads the target URL.
            wv.CoreWebView2.WebResourceResponseReceived += async (_, args) =>
            {
                try
                {
                    if (targetFaviconUrl == null) return;
                    if (!args.Request.Uri.Equals(targetFaviconUrl, StringComparison.OrdinalIgnoreCase)) return;
                    if (args.Response.StatusCode != 200) return;

                    using var stream = await args.Response.GetContentAsync();
                    if (stream == null || stream.Size == 0) return;

                    var size = (uint)stream.Size;
                    using var reader = new DataReader(stream);
                    await reader.LoadAsync(size);
                    var bytes = new byte[size];
                    reader.ReadBytes(bytes);

                    var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WinAssistant", "website-icons");
                    System.IO.Directory.CreateDirectory(dir);
                    var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16];
                    var file = System.IO.Path.Combine(dir, $"{hash}.ico");
                    await System.IO.File.WriteAllBytesAsync(file, bytes);

                    var bmp = new BitmapImage();
                    bmp.DecodePixelWidth = 64; bmp.DecodePixelHeight = 64;
                    bmp.UriSource = new Uri(file);
                    tcs.TrySetResult(new WebsiteMetadataHelper.WebsiteInfo(pageTitle, file, bmp));
                }
                catch { }
            };

            // Phase 1: load page, get title, find favicon URL.
            wv.NavigationCompleted += async (_, e) =>
            {
                try
                {
                    if (!e.IsSuccess) { tcs.TrySetResult(null); return; }

                    var tJson = await wv.CoreWebView2.ExecuteScriptAsync("document.title");
                    pageTitle = JsonSerializer.Deserialize<string>(tJson);

                    if (tcs.Task.IsCompleted) return; // already got favicon from phase 2
                    if (targetFaviconUrl != null) { tcs.TrySetResult(new WebsiteMetadataHelper.WebsiteInfo(pageTitle, null, null)); return; } // phase 2 nav completed but no favicon captured

                    // Phase 1: find the favicon URL.
                    var fJson = await wv.CoreWebView2.ExecuteScriptAsync("""
                        (function(){
                            var s=['link[rel*="apple-touch-icon"]','link[rel="fluid-icon"]','link[rel="shortcut icon"]','link[rel~="icon"]','link[rel="mask-icon"]'];
                            for(var i=0;i<s.length;i++){var e=document.querySelector(s[i]);if(e&&e.href)return e.href;}
                            var h=window.location.hostname.split('.');
                            if(h.length>2) return window.location.protocol+'//www.'+h.slice(-2).join('.')+'/favicon.ico';
                            return window.location.origin+'/favicon.ico';
                        })()
                        """);
                    var faviconUrl = JsonSerializer.Deserialize<string>(fJson);
                    if (string.IsNullOrEmpty(faviconUrl)) { tcs.TrySetResult(new WebsiteMetadataHelper.WebsiteInfo(pageTitle, null, null)); return; }

                    // Navigate to the favicon URL so Edge downloads it -> WebResourceResponseReceived fires.
                    targetFaviconUrl = faviconUrl;
                    wv.CoreWebView2.Navigate(faviconUrl);
                }
                catch (Exception ex) { tcs.TrySetResult(null); }
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            cts.Token.Register(() => tcs.TrySetResult(null));

            wv.CoreWebView2.Navigate(url);
            return await tcs.Task;
        }
        catch (Exception ex) { return null; }
        finally
        {
            try { wv?.Close(); if (wv?.Parent is Panel pa) pa.Children.Remove(wv); if (host?.Parent is Panel hp) hp.Children.Remove(host); } catch { }
        }
    }
}
