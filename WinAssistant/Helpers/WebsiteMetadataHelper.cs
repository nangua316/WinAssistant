using System.Net;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Svg;

namespace WinAssistant.Helpers;

/// <summary>Quick favicon downloader for the simple case (direct /favicon.ico or image URL).
/// For sites that block HttpClient, callers should fall back to WebView2FaviconHelper.</summary>
public static class WebsiteMetadataHelper
{
    private static readonly HttpClient _httpClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
    })
    { Timeout = TimeSpan.FromSeconds(8) };

    public record WebsiteInfo(string? Title, string? FaviconPath, ImageSource? FaviconSource);

    /// <summary>Download a favicon from a URL that's already an image (not an HTML page).
    /// Returns title=null since we didn't fetch HTML.</summary>
    public static async Task<WebsiteInfo> FetchIconDirectAsync(string iconUrl)
    {
        var icon = await DownloadFaviconAsync(iconUrl);
        return new WebsiteInfo(null, icon.Path, icon.Source);
    }

    /// <summary>Quick /favicon.ico check + try to get page title from HTML.</summary>
    public static async Task<WebsiteInfo> FetchAsync(string url)
    {
        if (!Uri.TryCreate(NormalizeUrl(url), UriKind.Absolute, out var uri))
            return new WebsiteInfo(null, null, null);

        // Direct image URL → download immediately.
        var ext = Path.GetExtension(uri.AbsolutePath);
        if (ext is ".ico" or ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".svg")
        {
            var dl = await DownloadFaviconAsync(url);
            return new WebsiteInfo(null, dl.Path, dl.Source);
        }

        // Quick /favicon.ico check (3s timeout, no HTML parsing).
        // Most sites serve the right icon here.
        var faviconUrl = $"{uri.Scheme}://{uri.Host}/favicon.ico";
        var ico = await DownloadFaviconAsync(faviconUrl, timeoutSeconds: 3);

        // Try to get the page title via quick HTML fetch (also 3s).
        string? title = null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var ct = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (resp.IsSuccessStatusCode && ct.Contains("html"))
            {
                var html = await resp.Content.ReadAsStringAsync(cts.Token);
                if (html.Length > 256 * 1024) html = html[..(256 * 1024)];
                var m = System.Text.RegularExpressions.Regex.Match(html, @"<title[^>]*>(.*?)</title>",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
                if (m.Success) title = WebUtility.HtmlDecode(m.Groups[1].Value.Trim());
            }
        }
        catch { }

        return new WebsiteInfo(!string.IsNullOrWhiteSpace(title) ? title : null, ico.Path, ico.Source);
    }

    private static string NormalizeUrl(string url)
    {
        url = url.Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url;
        return url;
    }

    private static async Task<(string? Path, ImageSource? Source)> DownloadFaviconAsync(string? url, int timeoutSeconds = 8)
    {
        if (string.IsNullOrEmpty(url)) return (null, null);
        try
        {
            Logger.Log("WebsiteMetadataHelper", $"Download: {url}");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            using var resp = await _httpClient.SendAsync(req, cts.Token);
            var ct = resp.Content.Headers.ContentType?.MediaType ?? "";
            var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token);
            if (!resp.IsSuccessStatusCode || bytes.Length == 0 || ct.Contains("html"))
                return (null, null);

            var tempDir = Path.Combine(Path.GetTempPath(), "WinAssistant", "website-icons");
            Directory.CreateDirectory(tempDir);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16];

            if (ct.Contains("svg") || url.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                using var stream = new MemoryStream(bytes);
                var svgDoc = SvgDocument.Open<SvgDocument>(stream);
                if (svgDoc == null) return (null, null);
                using var bmp = svgDoc.Draw(64, 64);
                if (bmp == null) return (null, null);
                var png = Path.Combine(tempDir, $"{hash}.png");
                bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                var bi = new BitmapImage();
                bi.DecodePixelWidth = 64; bi.DecodePixelHeight = 64;
                bi.UriSource = new Uri(png);
                return (png, bi);
            }

            var ext = InferExtension(ct, url);
            var file = Path.Combine(tempDir, $"{hash}{ext}");
            await File.WriteAllBytesAsync(file, bytes);
            var bitmap = new BitmapImage();
            bitmap.DecodePixelWidth = 64; bitmap.DecodePixelHeight = 64;
            bitmap.UriSource = new Uri(file);
            return (file, bitmap);
        }
        catch (Exception ex) { Logger.Log("WebsiteMetadataHelper", $"Download error: {ex.Message}"); }
        return (null, null);
    }

    private static string InferExtension(string ct, string url)
    {
        if (ct.Contains("png")) return ".png";
        if (ct.Contains("jpeg") || ct.Contains("jpg")) return ".jpg";
        if (ct.Contains("gif")) return ".gif";
        if (ct.Contains("webp")) return ".webp";
        if (ct.Contains("x-icon") || ct.Contains("ico")) return ".ico";
        var e = Path.GetExtension(new Uri(url).AbsolutePath);
        return !string.IsNullOrEmpty(e) && e.Length <= 5 ? e : ".png";
    }
}
