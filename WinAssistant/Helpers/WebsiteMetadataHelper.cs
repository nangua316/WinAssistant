using System.Net;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Svg;

namespace WinAssistant.Helpers;

/// <summary>Fetches website title + favicons via direct HTTP.
///
/// ONE HTTP request to download HTML (8s timeout, 32KB), then:
/// 1. Extract &lt;title&gt; from HTML
/// 2. Extract &lt;link rel="icon"&gt; URLs from HTML
/// 3. Download all icons (HTML-declared + host/www fallbacks) IN PARALLEL
///
/// Total: ~3-5 seconds. Simple, fast, no external APIs.</summary>
public static class WebsiteMetadataHelper
{
    private static readonly HttpClient _httpClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
    })
    { Timeout = TimeSpan.FromSeconds(10) };

    public record WebsiteInfo(string? Title, string? FaviconPath, ImageSource? FaviconSource);
    public record IconOption(string Label, string? Path, ImageSource? Source, int SortOrder);

    /// <summary>Fetch favicon: download HTML, extract icon links, download all in parallel.
    /// Fast (3-5s), no duplicate HTTP requests.</summary>
    public static async Task<List<IconOption>> FetchAllIconsAsync(string url)
    {
        Logger.Log("WebMeta", $"FetchIcons start: {url}");
        
        if (!Uri.TryCreate(NormalizeUrl(url), UriKind.Absolute, out var uri))
            return [];

        var host = uri.Host;
        var scheme = uri.Scheme;

        var hostParts = host.Split('.');
        var mainDomain = hostParts.Length > 2
            ? string.Join(".", hostParts[^2..])
            : host;
        var wwwDomain = host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? host
            : $"www.{mainDomain}";

        // Step 1: Download HTML, extract icon links (3s timeout — fail fast)
        var htmlIconUrls = await FetchHtmlIconLinksAsync(url);

        // Step 2: Build list of ALL icon URLs to download
        var allUrls = new List<(string Url, string Label, int Order)>();
        allUrls.AddRange(htmlIconUrls);

        var hostFavicon = $"{scheme}://{host}/favicon.ico";
        var wwwFavicon = $"{scheme}://{wwwDomain}/favicon.ico";

        if (!allUrls.Any(u => u.Url.Equals(hostFavicon, StringComparison.OrdinalIgnoreCase)))
            allUrls.Add((hostFavicon, $"{host}/favicon.ico", 100));
        if (!wwwDomain.Equals(host, StringComparison.OrdinalIgnoreCase) &&
            !allUrls.Any(u => u.Url.Equals(wwwFavicon, StringComparison.OrdinalIgnoreCase)))
            allUrls.Add((wwwFavicon, $"{wwwDomain}/favicon.ico", 101));

        // Step 3: Download ALL in parallel with 4s timeout each
        var tasks = allUrls.Select(u => DownloadAndLabelAsync(u.Url, u.Label, u.Order));
        var results = await Task.WhenAll(tasks);

        var icons = results
            .Where(r => r.Path != null && r.Source != null)
            .Select(r => new IconOption(r.Label, r.Path, r.Source, r.Order))
            .OrderBy(i => i.SortOrder)
            .ToList();

        Logger.Log("WebMeta", $"FetchIcons done: {icons.Count} icons, {allUrls.Count} candidates");
        return icons;
    }

    private static async Task<(string? Path, ImageSource? Source, string Label, int Order)>
        DownloadAndLabelAsync(string url, string label, int order)
    {
        var result = await DownloadFaviconAsync(url);
        return (result.Path, result.Source, label, order);
    }

    /// <summary>Download HTML and extract icon link URLs.</summary>
    private static async Task<List<(string Url, string Label, int Order)>> FetchHtmlIconLinksAsync(string url)
    {
        var result = new List<(string, string, int)>();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            req.Headers.TryAddWithoutValidation("Accept", "text/html");

            Logger.Log("WebsiteMetadataHelper", $"FetchHtmlIcons: sending request");
            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            Logger.Log("WebsiteMetadataHelper", $"FetchHtmlIcons: response {resp.StatusCode}");
            if (!resp.IsSuccessStatusCode) return result;

            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream);
            var buf = new char[32768];
            var read = await reader.ReadBlockAsync(buf, 0, buf.Length);
            var html = new string(buf, 0, read);
            Logger.Log("WebsiteMetadataHelper", $"FetchHtmlIcons: read {read} chars");

            // Match icon links: rel="icon" / "shortcut icon" / "apple-touch-icon" etc.
            var linkPattern = new Regex(
                @"<link[^>]*(?:rel=[""'](?<rel>[^""']*icon[^""']*)[""'])[^>]*href=[""'](?<href>[^""']+)[""'][^>]*>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var linkPattern2 = new Regex(
                @"<link[^>]*href=[""'](?<href>[^""']+)[""'][^>]*rel=[""'](?<rel>[^""']*icon[^""']*)[""'][^>]*>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            var links = new List<(string href, string rel, int size)>();

            void AddMatch(string href, string rel)
            {
                var size = 0;
                var sizesAttr = Regex.Match(html,
                    Regex.Escape(WebUtility.HtmlDecode(href)).Length > 0
                        ? $"href=[\"']{Regex.Escape(WebUtility.HtmlDecode(href))}[\"'][^>]*sizes=[\"'](\\d+)x(\\d+)[\"']"
                        : "sizes=[\"'](\\d+)x(\\d+)[\"']",
                    RegexOptions.IgnoreCase);
                if (sizesAttr.Success)
                    size = int.Parse(sizesAttr.Groups[1].Value) * int.Parse(sizesAttr.Groups[2].Value);
                links.Add((href, rel, size));
            }

            foreach (Match m in linkPattern.Matches(html))
                AddMatch(m.Groups["href"].Value, m.Groups["rel"].Value);
            foreach (Match m in linkPattern2.Matches(html))
                AddMatch(m.Groups["href"].Value, m.Groups["rel"].Value);

            // Sort: largest first, then apple-touch preferred
            links = links
                .OrderByDescending(l => l.size)
                .ThenByDescending(l => l.rel.Contains("apple-touch", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ToList();

            var baseUri = new Uri(url);
            int order = 0;
            foreach (var l in links)
            {
                var absUrl = ResolveUrl(baseUri, l.href);
                if (absUrl != null)
                {
                    var label = l.size > 0 ? $"{l.rel} ({l.size / 2}×{l.size / 2})" : l.rel;
                    result.Add((absUrl, label, order++));
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log("WebsiteMetadataHelper", $"FetchHtmlIconLinks error: {ex.Message}");
        }
        return result;
    }

    private static string? ResolveUrl(Uri baseUri, string href)
    {
        try
        {
            href = href.Trim();
            if (string.IsNullOrEmpty(href)) return null;
            if (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                href.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return href;
            if (href.StartsWith("//", StringComparison.OrdinalIgnoreCase))
                return $"https:{href}";
            if (href.StartsWith('/'))
                return $"{baseUri.Scheme}://{baseUri.Host}{href}";
            return new Uri(baseUri, href).ToString();
        }
        catch { return null; }
    }

    /// <summary>Fetch website title (reads first 32KB of HTML, 3s timeout).</summary>
    public static async Task<string?> FetchTitleAsync(string url)
    {
        if (!Uri.TryCreate(NormalizeUrl(url), UriKind.Absolute, out var uri))
            return null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            req.Headers.TryAddWithoutValidation("Accept", "text/html");

            Logger.Log("WebsiteMetadataHelper", $"FetchTitle: sending request to {url}");
            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            Logger.Log("WebsiteMetadataHelper", $"FetchTitle: response {resp.StatusCode}");

            if (!resp.IsSuccessStatusCode) return null;

            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream);
            var buf = new char[32768];
            var read = await reader.ReadBlockAsync(buf, 0, buf.Length);
            var html = new string(buf, 0, read);

            Logger.Log("WebsiteMetadataHelper", $"FetchTitle: read {read} chars");
            var m = Regex.Match(html, @"<title[^>]*>(.*?)</title>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (m.Success)
            {
                Logger.Log("WebsiteMetadataHelper", $"FetchTitle: found '{m.Groups[1].Value}'");
                return WebUtility.HtmlDecode(m.Groups[1].Value.Trim());
            }
            Logger.Log("WebsiteMetadataHelper", "FetchTitle: no <title> in HTML");
        }
        catch (Exception ex)
        {
            Logger.Log("WebsiteMetadataHelper", $"FetchTitle error: {ex.Message}");
        }
        return null;
    }

    /// <summary>Legacy: single best-effort fetch.</summary>
    public static async Task<WebsiteInfo> FetchAsync(string url)
    {
        if (!Uri.TryCreate(NormalizeUrl(url), UriKind.Absolute, out var uri))
            return new WebsiteInfo(null, null, null);

        var ext = Path.GetExtension(uri.AbsolutePath);
        if (ext is ".ico" or ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".svg")
        {
            var dl = await DownloadFaviconAsync(url);
            return new WebsiteInfo(null, dl.Path, dl.Source);
        }

        var title = await FetchTitleAsync(url);
        var icons = await FetchAllIconsAsync(url);
        var best = icons.FirstOrDefault();
        return new WebsiteInfo(title, best?.Path, best?.Source);
    }

    internal static string NormalizeUrl(string url)
    {
        url = url.Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url;
        return url;
    }

    /// <summary>Download an icon, save to cache, return path + ImageSource.
    /// Native decode size — display handles scaling.</summary>
    internal static async Task<(string? Path, ImageSource? Source)> DownloadFaviconAsync(string? url, int timeoutSeconds = 4)
    {
        if (string.IsNullOrEmpty(url)) return (null, null);
        try
        {
            Logger.Log("WebMeta", $"Download: {url}");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            using var resp = await _httpClient.SendAsync(req, cts.Token);
            var ct = resp.Content.Headers.ContentType?.MediaType ?? "";
            var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token);
            if (!resp.IsSuccessStatusCode || bytes.Length == 0 || ct.Contains("html"))
                return (null, null);

            if (bytes.Length < 200)
                return (null, null);

            var tempDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinAssistant", "website-icons");
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
                bi.UriSource = new Uri(png);
                return (png, bi);
            }

            var ext = InferExtension(ct, url);
            var file = Path.Combine(tempDir, $"{hash}{ext}");
            await File.WriteAllBytesAsync(file, bytes);
            var bitmap = new BitmapImage();
            bitmap.UriSource = new Uri(file);
            return (file, bitmap);
        }
        catch { }
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
