using System.Net;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Svg;

namespace WinAssistant.Helpers;

/// <summary>Fetches a website's title and favicon from its URL.</summary>
public static class WebsiteMetadataHelper
{
    private static readonly HttpClient _httpClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
        AutomaticDecompression = DecompressionMethods.GZip
                                 | DecompressionMethods.Deflate
                                 | DecompressionMethods.Brotli,
        UseCookies = true,
        CookieContainer = new CookieContainer()
    })
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    public record WebsiteInfo(string? Title, string? FaviconPath, ImageSource? FaviconSource);

    /// <summary>Best-effort fetch of the page title and favicon.</summary>
    public static async Task<WebsiteInfo> FetchAsync(string url)
    {
        Logger.Log("WebsiteMetadataHelper", $"FetchAsync: {url}");
        if (!Uri.TryCreate(NormalizeUrl(url), UriKind.Absolute, out var uri))
            return new WebsiteInfo(null, null, null);

        // Fast path: many sites (including bilibili) serve a real icon at /favicon.ico.
        // Try this first with a short timeout so we don't hang on slow HTML responses.
        var directFaviconUrl = $"{uri.Scheme}://{uri.Host}/favicon.ico";
        Logger.Log("WebsiteMetadataHelper", $"Fast path: {directFaviconUrl}");
        var direct = await DownloadFaviconAsync(directFaviconUrl, timeoutSeconds: 8);
        if (direct.Path != null)
        {
            Logger.Log("WebsiteMetadataHelper", $"Direct favicon success");
            var pageTitle = await FetchTitleAsync(uri);
            return new WebsiteInfo(pageTitle, direct.Path, direct.Source);
        }

        // Slow path: fetch HTML and parse declared icons / og:image.
        Logger.Log("WebsiteMetadataHelper", "Slow path: fetching HTML");
        var (html, title) = await FetchHtmlAsync(uri);
        if (!string.IsNullOrEmpty(html))
        {
            Logger.Log("WebsiteMetadataHelper", $"HTML fetched, length={html.Length}, title={title}");
            var declaredUrls = ExtractFaviconUrls(html, uri);
            Logger.Log("WebsiteMetadataHelper", $"Declared favicons={declaredUrls.Count}");
            foreach (var u in declaredUrls)
                Logger.Log("WebsiteMetadataHelper", $"  declared: {u}");

            var (faviconPath, faviconSource) = await TryFetchFaviconAsync(html, uri, declaredUrls);
            Logger.Log("WebsiteMetadataHelper", $"Result faviconPath={faviconPath}");
            return new WebsiteInfo(title, faviconPath, faviconSource);
        }

        return new WebsiteInfo(null, null, null);
    }

    private static async Task<(string? Html, string? Title)> FetchHtmlAsync(Uri uri)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
            request.Headers.TryAddWithoutValidation("Accept",
                "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
            request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            Logger.Log("WebsiteMetadataHelper", $"FetchHtml status={response.StatusCode}, contentType={contentType}");
            if (!response.IsSuccessStatusCode) return (null, null);
            if (!contentType.Contains("html", StringComparison.OrdinalIgnoreCase)) return (null, null);

            var html = await response.Content.ReadAsStringAsync(cts.Token);
            if (html.Length > 256 * 1024)
                html = html[..(256 * 1024)];

            var title = ExtractTitle(html);
            return (html, title);
        }
        catch (OperationCanceledException)
        {
            Logger.Log("WebsiteMetadataHelper", "FetchHtml canceled (timeout)");
        }
        catch (Exception ex)
        {
            Logger.Log("WebsiteMetadataHelper", $"FetchHtml error: {ex.Message}");
        }
        return (null, null);
    }

    private static async Task<string?> FetchTitleAsync(Uri uri)
    {
        var (_, title) = await FetchHtmlAsync(uri);
        return title;
    }

    private static async Task<(string? Path, ImageSource? Source)> TryFetchFaviconAsync(string html, Uri pageUri, List<string> declaredUrls)
    {
        // 1. Try icons explicitly declared in the HTML (prefer non-SVG, larger touch icons).
        foreach (var url in declaredUrls)
        {
            var result = await DownloadFaviconAsync(url, timeoutSeconds: 8);
            if (result.Path != null) return result;
        }

        // 2. Fallback to the well-known /favicon.ico location.
        var fallbackUrl = $"{pageUri.Scheme}://{pageUri.Host}/favicon.ico";
        Logger.Log("WebsiteMetadataHelper", $"Trying fallback favicon: {fallbackUrl}");
        var fallback = await DownloadFaviconAsync(fallbackUrl, timeoutSeconds: 8);
        if (fallback.Path != null) return fallback;

        // 3. Last resort: use Open Graph or Twitter card image.
        var ogImage = ExtractMetaImage(html, pageUri);
        if (!string.IsNullOrEmpty(ogImage))
        {
            Logger.Log("WebsiteMetadataHelper", $"Trying og:image: {ogImage}");
            return await DownloadFaviconAsync(ogImage, timeoutSeconds: 8);
        }

        return (null, null);
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
        var match = Regex.Match(html, @"<title[^>]*>(.*?)</title>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        if (!match.Success) return null;

        var title = match.Groups[1].Value.Trim();
        title = WebUtility.HtmlDecode(title);
        return string.IsNullOrWhiteSpace(title) ? null : title;
    }

    private static List<string> ExtractFaviconUrls(string html, Uri pageUri)
    {
        var candidates = new List<(string Url, int Priority)>();

        var linkMatches = Regex.Matches(html,
            @"<link\s+([^>]*)>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        foreach (Match link in linkMatches)
        {
            var attrs = link.Groups[1].Value;
            var relMatch = Regex.Match(attrs, @"rel=[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            var hrefMatch = Regex.Match(attrs, @"href=[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            if (!relMatch.Success || !hrefMatch.Success) continue;

            var rel = relMatch.Groups[1].Value;
            var href = hrefMatch.Groups[1].Value.Trim();
            if (string.IsNullOrEmpty(href)) continue;

            var priority = rel.ToLowerInvariant() switch
            {
                var r when r.Contains("apple-touch-icon") => 1,
                var r when r.Contains("fluid-icon") => 2,
                var r when r.Contains("shortcut icon") => 3,
                var r when r.Contains("icon") => 4,
                var r when r.Contains("mask-icon") => 5,
                _ => 100
            };

            if (priority >= 100) continue;

            if (Uri.TryCreate(href, UriKind.Absolute, out var absolute))
                candidates.Add((absolute.ToString(), priority));
            else if (Uri.TryCreate(pageUri, href, out var relative))
                candidates.Add((relative.ToString(), priority));
        }

        return candidates
            .OrderBy(c => c.Priority)
            .Select(c => c.Url)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? ExtractMetaImage(string html, Uri pageUri)
    {
        var match = Regex.Match(html,
            @"<meta[^>]*property=[""']og:image[""'][^>]*content=[""']([^""']+)[""'][^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        if (!match.Success)
        {
            match = Regex.Match(html,
                @"<meta[^>]*content=[""']([^""']+)[""'][^>]*property=[""']og:image[""'][^>]*>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        }
        if (match.Success)
        {
            var href = match.Groups[1].Value.Trim();
            if (Uri.TryCreate(href, UriKind.Absolute, out var absolute)) return absolute.ToString();
            if (Uri.TryCreate(pageUri, href, out var relative)) return relative.ToString();
        }

        match = Regex.Match(html,
            @"<meta[^>]*name=[""']twitter:image[""'][^>]*content=[""']([^""']+)[""'][^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        if (!match.Success)
        {
            match = Regex.Match(html,
                @"<meta[^>]*content=[""']([^""']+)[""'][^>]*name=[""']twitter:image[""'][^>]*>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        }
        if (match.Success)
        {
            var href = match.Groups[1].Value.Trim();
            if (Uri.TryCreate(href, UriKind.Absolute, out var absolute)) return absolute.ToString();
            if (Uri.TryCreate(pageUri, href, out var relative)) return relative.ToString();
        }

        return null;
    }

    private static async Task<(string? Path, ImageSource? Source)> DownloadFaviconAsync(string? faviconUrl, int timeoutSeconds = 10)
    {
        if (string.IsNullOrEmpty(faviconUrl)) return (null, null);

        try
        {
            Logger.Log("WebsiteMetadataHelper", $"DownloadFaviconAsync: {faviconUrl}");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var request = new HttpRequestMessage(HttpMethod.Get, faviconUrl);
            request.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            request.Headers.TryAddWithoutValidation("Accept",
                "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8");
            request.Headers.TryAddWithoutValidation("Referer", new Uri(faviconUrl).GetLeftPart(UriPartial.Authority));

            using var response = await _httpClient.SendAsync(request, cts.Token);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token);
            Logger.Log("WebsiteMetadataHelper", $"DownloadFaviconAsync status={response.StatusCode}, contentType={contentType}, length={bytes.Length}");
            if (!response.IsSuccessStatusCode) return (null, null);
            if (bytes.Length == 0) return (null, null);

            // Reject HTML responses masquerading as favicons (e.g. SPA fallback pages).
            if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Log("WebsiteMetadataHelper", $"Skipping HTML response for {faviconUrl}");
                return (null, null);
            }

            var tempDir = System.IO.Path.Combine(Path.GetTempPath(), "WinAssistant", "website-icons");
            Directory.CreateDirectory(tempDir);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16];

            // SVG needs conversion because WinUI BitmapImage cannot render SVG directly.
            if (contentType.Contains("svg", StringComparison.OrdinalIgnoreCase)
                || faviconUrl.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                var converted = ConvertSvgToPng(bytes, tempDir, hash);
                if (converted.HasValue) return converted.Value;
                return (null, null);
            }

            var ext = InferExtension(contentType, faviconUrl);
            var tempFile = System.IO.Path.Combine(tempDir, $"{hash}{ext}");
            await File.WriteAllBytesAsync(tempFile, bytes);

            var bitmap = new BitmapImage();
            bitmap.UriSource = new Uri(tempFile);
            return (tempFile, bitmap);
        }
        catch (OperationCanceledException)
        {
            Logger.Log("WebsiteMetadataHelper", $"DownloadFaviconAsync canceled: {faviconUrl}");
        }
        catch (Exception ex)
        {
            Logger.Log("WebsiteMetadataHelper", $"Favicon download failed for {faviconUrl}: {ex.Message}");
        }

        return (null, null);
    }

    private static (string? Path, ImageSource? Source)? ConvertSvgToPng(byte[] svgBytes, string tempDir, string hash)
    {
        try
        {
            using var stream = new MemoryStream(svgBytes);
            var svgDocument = SvgDocument.Open<SvgDocument>(stream);
            if (svgDocument == null) return null;

            using var bitmap = svgDocument.Draw(64, 64);
            if (bitmap == null) return null;

            var pngPath = System.IO.Path.Combine(tempDir, $"{hash}.png");
            bitmap.Save(pngPath, System.Drawing.Imaging.ImageFormat.Png);

            var bitmapImage = new BitmapImage();
            bitmapImage.UriSource = new Uri(pngPath);
            return (pngPath, bitmapImage);
        }
        catch (Exception ex)
        {
            Logger.Log("WebsiteMetadataHelper", $"SVG conversion failed: {ex.Message}");
        }
        return null;
    }

    private static string InferExtension(string contentType, string url)
    {
        if (contentType.Contains("png", StringComparison.OrdinalIgnoreCase)) return ".png";
        if (contentType.Contains("jpeg", StringComparison.OrdinalIgnoreCase) || contentType.Contains("jpg", StringComparison.OrdinalIgnoreCase)) return ".jpg";
        if (contentType.Contains("gif", StringComparison.OrdinalIgnoreCase)) return ".gif";
        if (contentType.Contains("webp", StringComparison.OrdinalIgnoreCase)) return ".webp";
        if (contentType.Contains("x-icon", StringComparison.OrdinalIgnoreCase) || contentType.Contains("ico", StringComparison.OrdinalIgnoreCase)) return ".ico";

        var ext = System.IO.Path.GetExtension(new Uri(url).AbsolutePath);
        if (!string.IsNullOrEmpty(ext) && ext.Length <= 5) return ext;

        return ".png";
    }
}
