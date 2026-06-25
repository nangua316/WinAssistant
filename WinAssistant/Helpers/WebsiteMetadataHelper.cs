using System.Net;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace WinAssistant.Helpers;

/// <summary>Fetches a website's title and favicon from its URL.</summary>
public static class WebsiteMetadataHelper
{
    private static readonly HttpClient _httpClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5
    })
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    public record WebsiteInfo(string? Title, string? FaviconPath, ImageSource? FaviconSource);

    /// <summary>Best-effort fetch of the page title and favicon.</summary>
    public static async Task<WebsiteInfo> FetchAsync(string url)
    {
        if (!Uri.TryCreate(NormalizeUrl(url), UriKind.Absolute, out var uri))
            return new WebsiteInfo(null, null, null);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
                return new WebsiteInfo(null, null, null);

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                return new WebsiteInfo(null, null, null);

            // Limit read size to avoid huge pages / streaming responses.
            var html = await response.Content.ReadAsStringAsync();
            if (html.Length > 256 * 1024)
                html = html[..(256 * 1024)];

            var title = ExtractTitle(html);
            var (faviconPath, faviconSource) = await TryFetchFaviconAsync(html, uri);

            return new WebsiteInfo(title, faviconPath, faviconSource);
        }
        catch (Exception ex)
        {
            Logger.Log("WebsiteMetadataHelper", $"Fetch failed for {url}: {ex.Message}");
        }

        return new WebsiteInfo(null, null, null);
    }

    private static async Task<(string? Path, ImageSource? Source)> TryFetchFaviconAsync(string html, Uri pageUri)
    {
        // 1. Try the icon explicitly declared in the HTML.
        var declaredUrl = ExtractFaviconUrl(html, pageUri);
        if (!string.IsNullOrEmpty(declaredUrl))
        {
            var declared = await DownloadFaviconAsync(declaredUrl);
            if (declared.Path != null) return declared;
        }

        // 2. Fallback to the well-known /favicon.ico location.
        var fallbackUrl = $"{pageUri.Scheme}://{pageUri.Host}/favicon.ico";
        Logger.Log("WebsiteMetadataHelper", $"Trying fallback favicon: {fallbackUrl}");
        return await DownloadFaviconAsync(fallbackUrl);
    }

    private static string NormalizeUrl(string url)
    {
        url = url.Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }
        return url;
    }

    private static string? ExtractTitle(string html)
    {
        // Match <title>...</title>, case-insensitive, allowing whitespace.
        var match = Regex.Match(html, @"<title[^>]*>(.*?)</title>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        if (!match.Success) return null;

        var title = match.Groups[1].Value.Trim();
        // Decode common HTML entities.
        title = WebUtility.HtmlDecode(title);
        return string.IsNullOrWhiteSpace(title) ? null : title;
    }

    private static string? ExtractFaviconUrl(string html, Uri pageUri)
    {
        // Look for <link rel="icon" href="..."> or rel="shortcut icon".
        var match = Regex.Match(html,
            @"<link[^>]*rel=[""'](?:shortcut\s+icon|icon|apple-touch-icon)[""'][^>]*href=[""']([^""']+)[""'][^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        if (!match.Success)
        {
            // Try href before rel.
            match = Regex.Match(html,
                @"<link[^>]*href=[""']([^""']+)[""'][^>]*rel=[""'](?:shortcut\s+icon|icon|apple-touch-icon)[""'][^>]*>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        }

        if (!match.Success) return null;

        var href = match.Groups[1].Value.Trim();
        if (string.IsNullOrEmpty(href)) return null;

        if (Uri.TryCreate(href, UriKind.Absolute, out var absolute)) return absolute.ToString();
        if (Uri.TryCreate(pageUri, href, out var relative)) return relative.ToString();
        return null;
    }

    private static async Task<(string? Path, ImageSource? Source)> DownloadFaviconAsync(string? faviconUrl)
    {
        if (string.IsNullOrEmpty(faviconUrl)) return (null, null);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, faviconUrl);
            request.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            using var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return (null, null);

            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length == 0) return (null, null);

            var tempDir = System.IO.Path.Combine(Path.GetTempPath(), "WinAssistant", "website-icons");
            Directory.CreateDirectory(tempDir);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16];
            var ext = Path.GetExtension(new Uri(faviconUrl).AbsolutePath);
            if (string.IsNullOrEmpty(ext) || ext.Length > 5) ext = ".png";
            var tempFile = System.IO.Path.Combine(tempDir, $"{hash}{ext}");
            await File.WriteAllBytesAsync(tempFile, bytes);

            var bitmap = new BitmapImage();
            bitmap.UriSource = new Uri(tempFile);
            return (tempFile, bitmap);
        }
        catch (Exception ex)
        {
            Logger.Log("WebsiteMetadataHelper", $"Favicon download failed for {faviconUrl}: {ex.Message}");
        }

        return (null, null);
    }
}
