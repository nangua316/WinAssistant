using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using System.Text.Json;
using Windows.Storage.Streams;

namespace WinAssistant.Helpers;

/// <summary>Fetches website title + favicon using Edge's own browser engine.
/// Approach: let the page fully load + JS settle, then read title from DOM
/// and get the favicon via Edge's native favicon API (same as what shows in the tab).
/// For pages that set icons dynamically, we wait and retry automatically.</summary>
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

            wv.NavigationCompleted += async (_, e) =>
            {
                if (!e.IsSuccess) { tcs.TrySetResult(null); return; }

                // Wait for JS to settle (SPAs set title+favicon dynamically).
                await Task.Delay(2000);

                // Get the final title (after JS executes).
                var tJson = await wv.CoreWebView2.ExecuteScriptAsync("document.title");
                var title = JsonSerializer.Deserialize<string>(tJson);
                Logger.Log("WebView2FaviconHelper", $"title='{title}'");

                // Get favicon via Edge's own API — this is what the tab uses.
                string? favFile = null;
                BitmapImage? favBmp = null;

                for (int retry = 0; retry < 3; retry++)
                {
                    try
                    {
                        using var s = await wv.CoreWebView2.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
                        if (s != null && s.Size > 100) // >100 bytes = real icon
                        {
                            var size = (uint)s.Size;
                            using var r = new DataReader(s);
                            await r.LoadAsync(size);
                            var bytes = new byte[size];
                            r.ReadBytes(bytes);

                            var dir = Path.Combine(Path.GetTempPath(), "WinAssistant", "website-icons");
                            Directory.CreateDirectory(dir);
                            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16];
                            var file = Path.Combine(dir, $"{hash}.png");
                            await File.WriteAllBytesAsync(file, bytes);

                            var bmp = new BitmapImage();
                            bmp.DecodePixelWidth = 64; bmp.DecodePixelHeight = 64;
                            bmp.UriSource = new Uri(file);
                            favFile = file; favBmp = bmp;
                            Logger.Log("WebView2FaviconHelper", $"favicon via API: {bytes.Length}B");
                            break;
                        }
                    }
                    catch { }

                    // Not ready yet — wait and retry (dynamic favicon).
                    await Task.Delay(1500);
                }

                // If native API failed, try manual download via JS + WebResourceResponseReceived.
                if (favFile == null)
                {
                    Logger.Log("WebView2FaviconHelper", "favicon API failed, trying manual fetch");
                    var fJson = await wv.CoreWebView2.ExecuteScriptAsync("""
                        (function(){
                            var s=['link[rel*="apple-touch-icon"]','link[rel="fluid-icon"]','link[rel="shortcut icon"]','link[rel~="icon"]','link[rel="mask-icon"]'];
                            for(var i=0;i<s.length;i++){var e=document.querySelector(s[i]);if(e&&e.href)return e.href;}
                            var h=window.location.hostname.split('.');
                            if(h.length>2)return window.location.protocol+'//www.'+h.slice(-2).join('.')+'/favicon.ico';
                            return window.location.origin+'/favicon.ico';
                        })()
                        """);
                    var faviconUrl = JsonSerializer.Deserialize<string>(fJson);
                    if (!string.IsNullOrEmpty(faviconUrl))
                    {
                        Logger.Log("WebView2FaviconHelper", $"manual favicon URL: {faviconUrl}");

                        // Register response capture before navigating.
                        TaskCompletionSource<WebsiteMetadataHelper.WebsiteInfo?> manual = new();
                        wv.CoreWebView2.WebResourceResponseReceived += async (_, args) =>
                        {
                            try
                            {
                                if (!args.Request.Uri.Equals(faviconUrl, StringComparison.OrdinalIgnoreCase)) return;
                                if (args.Response.StatusCode != 200) return;
                                using var stream = await args.Response.GetContentAsync();
                                if (stream == null || stream.Size == 0) return;
                                var sz = (uint)stream.Size;
                                using var reader = new DataReader(stream);
                                await reader.LoadAsync(sz);
                                var bytes = new byte[sz];
                                reader.ReadBytes(bytes);
                                var dir = Path.Combine(Path.GetTempPath(), "WinAssistant", "website-icons");
                                Directory.CreateDirectory(dir);
                                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16];
                                var file = Path.Combine(dir, $"{hash}.ico");
                                await File.WriteAllBytesAsync(file, bytes);
                                var bmp = new BitmapImage();
                                bmp.DecodePixelWidth = 64; bmp.DecodePixelHeight = 64;
                                bmp.UriSource = new Uri(file);
                                manual.TrySetResult(new WebsiteMetadataHelper.WebsiteInfo(title, file, bmp));
                            }
                            catch { }
                        };

                        wv.CoreWebView2.Navigate(faviconUrl);

                        // Wait for the manual download (6s timeout).
                        using var cts2 = new CancellationTokenSource(6000);
                        cts2.Token.Register(() => manual.TrySetResult(null));
                        var m = await manual.Task;
                        if (m != null && m.FaviconSource != null)
                        {
                            tcs.TrySetResult(new WebsiteMetadataHelper.WebsiteInfo(title, m.FaviconPath, m.FaviconSource));
                            return;
                        }
                    }
                }

                tcs.TrySetResult(new WebsiteMetadataHelper.WebsiteInfo(title, favFile, favBmp));
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
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
