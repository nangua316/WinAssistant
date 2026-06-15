using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml.Media.Imaging;
using Toolbelt.Drawing;
using Windows.Storage.Streams;

namespace WinAssistant.Helpers;

public static class IconHelper
{
    /// <summary>
    /// Save app icon to a temp PNG file (for disk caching).
    /// Prefers file path over AUMID — SHGetFileInfo works for all apps including Store apps.
    /// </summary>
    public static string? ExtractAppIconToAppData(string filePath, int targetSize = 64, string aumid = "")
    {
        try
        {
            using var ms = PreferFileExtraction(filePath, aumid, targetSize);
            if (ms == null) return null;

            var tempDir = System.IO.Path.Combine(Path.GetTempPath(), "WinAssistant", "icons");
            Directory.CreateDirectory(tempDir);

            // Use a stable hash of the app path/AUMID as filename so the same app
            // reuses its cached icon instead of creating a new file every time.
            // SHA256 avoids collision risk that string.GetHashCode() carries.
            var key = !string.IsNullOrEmpty(filePath) ? filePath : aumid;
            var keyBytes = Encoding.UTF8.GetBytes(key);
            var hashBytes = SHA256.HashData(keyBytes);
            var hash = Convert.ToHexString(hashBytes)[..16]; // first 16 hex chars
            var tempFile = System.IO.Path.Combine(tempDir, $"{hash}.png");
            var stampFile = tempFile + ".timestamp";

            // Cache invalidation: check source file's last-write time.
            // If the source was updated, re-extract even if the cache file exists.
            bool cacheValid = File.Exists(tempFile);
            if (cacheValid && File.Exists(stampFile))
            {
                var cachedStamp = File.ReadAllText(stampFile);
                var sourceStamp = File.GetLastWriteTimeUtc(filePath).Ticks.ToString();
                if (!cachedStamp.Equals(sourceStamp, StringComparison.Ordinal))
                    cacheValid = false;
            }

            if (!cacheValid)
            {
                File.WriteAllBytes(tempFile, ms.ToArray());
                // Store the source's write time so we know when to re-extract.
                if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                    File.WriteAllText(stampFile, File.GetLastWriteTimeUtc(filePath).Ticks.ToString());
            }
            return tempFile;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extract app icon to PNG byte array. Prefers file path over AUMID.
    /// </summary>
    public static byte[]? ExtractIconBytes(string filePath, int targetSize = 64, string aumid = "")
    {
        try
        {
            using var ms = PreferFileExtraction(filePath, aumid, targetSize);
            if (ms == null)
            {
                Logger.Log("IconHelper",$"ExtractIconBytes: failed");
                return null;
            }
            var result = ms.ToArray();
            return result;
        }
        catch (Exception ex)
        {
            Logger.Log("IconHelper",$"ExtractIconBytes: error {ex.Message}");
            return null;
        }
    }

    // Keeps the IRandomAccessStream alive as long as the BitmapImage lives.
    // Without this, the InMemoryRandomAccessStream may be GC'd too early,
    // causing BitmapImage to lose its source data when it tries to decode.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BitmapImage, object> _streamKeepAlive = new();

    private static async Task<bool> PopulateBitmapFromPngBytesAsync(BitmapImage bitmap, byte[] pngBytes, int decodePixelWidth)
    {
        var ras = new InMemoryRandomAccessStream();
        var writer = new DataWriter(ras);
        writer.WriteBytes(pngBytes);
        await writer.StoreAsync();
        writer.DetachStream(); // prevents DataWriter from closing ras on Dispose
        ras.Seek(0);

        bitmap.DecodePixelWidth = decodePixelWidth;
        await bitmap.SetSourceAsync(ras);

        _streamKeepAlive.Add(bitmap, ras);
        Logger.Log("IconHelper",$"PopulateBitmap OK: pw={bitmap.PixelWidth} ph={bitmap.PixelHeight}");
        return true;
    }

    /// <summary>
    /// Extract app icon to a BitmapImage directly (no temp file).
    /// Call on UI thread via DispatcherQueue.
    /// </summary>
    public static async Task<BitmapImage?> ExtractAppIconAsync(string filePath, int targetSize = 64, string aumid = "")
    {
        try
        {
            using var pngMs = PreferFileExtraction(filePath, aumid, targetSize);
            if (pngMs == null) return null;

            var bitmap = new BitmapImage();
            if (await PopulateBitmapFromPngBytesAsync(bitmap, pngMs.ToArray(), Math.Min(targetSize, 64)))
                return bitmap;
            return null;
        }
        catch (Exception ex)
        {
            Logger.Log("IconHelper",$"ExtractAppIconAsync error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Prefer file path extraction over AUMID. Multiple fallback strategies.
    /// </summary>
    private static MemoryStream? PreferFileExtraction(string filePath, string aumid, int targetSize)
    {
        MemoryStream? shResult = null;

        if (!string.IsNullOrEmpty(filePath) && (File.Exists(filePath) || Directory.Exists(filePath)))
        {
            bool isDirectory = !File.Exists(filePath) && Directory.Exists(filePath);

            // Files without an extension (e.g. MSI "ProductIcon") confuse SHGetFileInfo
            // into returning a generic document icon.  Copy to a temp .ico path.
            if (!isDirectory && !Path.HasExtension(filePath))
            {
                try
                {
                    var tempIco = System.IO.Path.Combine(
                        System.IO.Path.GetTempPath(), "WinAssistant", "icocache",
                        $"_{System.IO.Path.GetFileName(filePath)}.ico");
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(tempIco)!);
                    if (!File.Exists(tempIco))
                        File.Copy(filePath, tempIco, overwrite: false);
                    if (File.Exists(tempIco))
                    {
                        Logger.Log("IconHelper", $"Using .ico copy: {tempIco}");
                        filePath = tempIco;
                    }
                }
                catch { }
            }

            // For directories, skip IShellItemImageFactory (it returns inconsistent
            // thumbnails that vary by folder contents) and use SHGetFileInfo directly
            // with FILE_ATTRIBUTE_DIRECTORY for a consistent generic folder icon.
            if (!isDirectory)
            {
                // Try IShellItemImageFactory first — best quality
                var shellResult = ExtractIconViaShellFactory(filePath, targetSize);
                if (shellResult != null) return shellResult;
            }

            // For Store apps in WindowsApps, the exe is often a stub without embedded icons.
            // Prefer AUMID extraction for them; for regular Win32 exes, prefer file-based icon.
            bool isStoreAppStub = filePath.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase);

            if (isStoreAppStub && !string.IsNullOrEmpty(aumid))
            {
                // Try AUMID extraction first (Store stub exes have no real icons)
                Logger.Log("IconHelper",$"Trying AUMID: {aumid}");
                var aumidResult = ExtractIconFromAumidToPngStream(aumid, targetSize);
                if (aumidResult != null) return aumidResult;
            }

            // For .ico files, load directly (SHGetFileInfo returns the file-type icon, not the content)
            if (filePath.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Log("IconHelper",$"Loading .ico directly: {filePath}");
                var icoResult = ExtractIcoFile(filePath, targetSize);
                if (icoResult != null) return icoResult;
            }

            // System image list — 256x256 icons from the shell cache (EverythingToolbar approach)
            Logger.Log("IconHelper",$"ImageList for {filePath}");
            var imgResult = ExtractIconViaImageList(filePath, targetSize);
            if (imgResult != null) return imgResult;

            // PrivateExtractIcons — requests the exact size (covers most .exe/.dll)
            Logger.Log("IconHelper",$"PrivateExtractIcons for {filePath} size={targetSize}");
            shResult = ExtractIconViaPrivateExtract(filePath, targetSize);
            if (shResult != null) return shResult;

            // SHGetFileInfo for Win32 exe icons (32x32 fallback)
            shResult = ExtractIconToPngStream(filePath, targetSize);
            if (shResult != null) return shResult;

            // AUMID fallback for non-Store apps
            if (!isStoreAppStub && !string.IsNullOrEmpty(aumid))
            {
                Logger.Log("IconHelper",$"Trying AUMID: {aumid}");
                var aumidResult = ExtractIconFromAumidToPngStream(aumid, targetSize);
                if (aumidResult != null) return aumidResult;
            }

            // Last resort: try ExtractIconEx
            var exResult = ExtractIconExToPngStream(filePath, targetSize);
            if (exResult != null) return exResult;

            // Last last resort: System.Drawing.Icon.ExtractAssociatedIcon (catches exes
            // where SHGetFileInfo/ExtractIconEx fail, e.g. some Electron apps like CC Switch)
            Logger.Log("IconHelper",$"Trying ExtractAssociatedIcon for {filePath}");
            try
            {
                var extracted = System.Drawing.Icon.ExtractAssociatedIcon(filePath);
                if (extracted != null)
                {
                    using var resized = new System.Drawing.Bitmap(targetSize, targetSize);
                    using var g = System.Drawing.Graphics.FromImage(resized);
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.Clear(System.Drawing.Color.Transparent);
                    g.DrawIcon(extracted, new System.Drawing.Rectangle(0, 0, targetSize, targetSize));
                    var ms = new MemoryStream();
                    resized.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    ms.Position = 0;
                    Logger.Log("IconHelper",$"ExtractAssociatedIcon OK: {filePath}");
                    return ms;
                }
            }
            catch { }

            // Last resort: try SHGetFileInfo with SHGFI_SMALLICON (some exes only have 16x16 icon)
            Logger.Log("IconHelper",$"Trying SHGFI_SMALLICON for {filePath}");
            try
            {
                var shfi = new SHFILEINFO();
                var ret = SHGetFileInfoW(filePath, 0, ref shfi, Marshal.SizeOf<SHFILEINFO>(), 0x100 | 0x1);
                if (ret != nint.Zero && shfi.hIcon != nint.Zero)
                {
                    using var icon = System.Drawing.Icon.FromHandle(shfi.hIcon);
                    using var resized = new System.Drawing.Bitmap(targetSize, targetSize);
                    using var g = System.Drawing.Graphics.FromImage(resized);
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.Clear(System.Drawing.Color.Transparent);
                    g.DrawIcon(icon, new System.Drawing.Rectangle(0, 0, targetSize, targetSize));
                    DestroyIcon(shfi.hIcon);
                    var ms = new MemoryStream();
                    resized.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    ms.Position = 0;
                    Logger.Log("IconHelper",$"SHGFI_SMALLICON OK: {filePath}");
                    return ms;
                }
            }
            catch { }

            // Final resort: TsudaKageyu/IconExtractor — direct PE resource parsing
            // Catches exes where Win32 shell APIs fail (e.g. some packed/compressed exes)
            if (filePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Log("IconHelper",$"IconExtractor for {filePath}");
                try
                {
                    using var icoStream = new MemoryStream();
                    IconExtractor.Extract1stIconTo(filePath, icoStream);
                    if (icoStream.Length > 0)
                    {
                        icoStream.Position = 0;
                        using var extracted = new System.Drawing.Icon(icoStream);
                        using var resized = new System.Drawing.Bitmap(targetSize, targetSize);
                        using var g = System.Drawing.Graphics.FromImage(resized);
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.Clear(System.Drawing.Color.Transparent);
                        g.DrawIcon(extracted, new System.Drawing.Rectangle(0, 0, targetSize, targetSize));
                        var ms = new MemoryStream();
                        resized.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                        ms.Position = 0;
                        Logger.Log("IconHelper",$"IconExtractor OK: {filePath}");
                        return ms;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log("IconHelper", $"IconExtractor error: {ex.Message}");
                }
            }
        }

        // AUMID fallback when file path is unavailable
        if (!string.IsNullOrEmpty(aumid))
        {
            Logger.Log("IconHelper",$"AUMID fallback for: {aumid}");
            return ExtractIconFromAumidToPngStream(aumid, targetSize);
        }

        return null;
    }

    /// <summary>
    /// Create a BitmapImage from PNG bytes (e.g. from ExtractIconBytes).
    /// </summary>
    public static async Task<BitmapImage?> CreateBitmapFromBytesAsync(byte[] pngBytes, int decodePixelWidth = 48)
    {
        try
        {
            var bitmap = new BitmapImage();
            if (await PopulateBitmapFromPngBytesAsync(bitmap, pngBytes, decodePixelWidth))
                return bitmap;
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Compute icon size scaled for the window's DPI.
    /// </summary>
    public static int GetScaledIconSize(int baseSize, nint hwnd)
    {
        try
        {
            var dpi = GetDpiForWindow(hwnd);
            return (int)(baseSize * dpi / 96.0);
        }
        catch { return baseSize; }
    }

    [DllImport("user32.dll")]
    private static extern int GetDpiForWindow(nint hwnd);

    public static void CleanupTempIcons()
    {
        try
        {
            var tempDir = System.IO.Path.Combine(Path.GetTempPath(), "WinAssistant");
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
        catch { }
    }

    /// <summary>
    /// Extract icon from AUMID (Windows Store app) and return PNG bytes in a MemoryStream.
    /// </summary>
    private static MemoryStream? ExtractIconFromAumidToPngStream(string aumid, int targetSize)
    {
        try
        {
            Logger.Log("IconHelper",$"尝试获取 UWP 图标: {aumid}");
            
            // 方案3: 使用 SHParseDisplayName 然后用 SHGetFileInfo 获取图标
            nint pidl = nint.Zero;
            uint pchEaten = 0;
            uint pdwAttributes = 0;
            var parsingPath = $"shell:AppsFolder\\{aumid}";
            
            var hr = SHParseDisplayName(parsingPath, nint.Zero, out pidl, 0, ref pdwAttributes);
            if (hr != 0 || pidl == nint.Zero)
            {
                Logger.Log("IconHelper",$"SHParseDisplayName 失败: 0x{hr:X8}");
                return null;
            }

            try
            {
                // 用 SHGetFileInfo 获取图标
                var shfi = new SHFILEINFO();
                var ret = SHGetFileInfoW(pidl, 0, ref shfi, Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON | SHGFI_PIDL);
                
                if (ret == nint.Zero || shfi.hIcon == nint.Zero)
                {
                    Logger.Log("IconHelper", "SHGetFileInfo 失败");
                    return null;
                }

                try
                {
                    using var icon = System.Drawing.Icon.FromHandle(shfi.hIcon);
                    using var resized = new System.Drawing.Bitmap(targetSize, targetSize);
                    using var g = System.Drawing.Graphics.FromImage(resized);
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.Clear(System.Drawing.Color.Transparent);
                    g.DrawIcon(icon, new System.Drawing.Rectangle(0, 0, targetSize, targetSize));

                    var ms = new MemoryStream();
                    resized.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    ms.Position = 0;
                    Logger.Log("IconHelper",$"成功获取 UWP 图标: {aumid}");
                    return ms;
                }
                finally
                {
                    DestroyIcon(shfi.hIcon);
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(pidl);
            }
        }
        catch (Exception ex)
        {
            Logger.Log("IconHelper",$"ExtractIconFromAumidToPngStream error: {ex.Message}\n{ex.StackTrace}");
            return null;
        }
    }

    /// <summary>
    /// Extract icon via IShellItemImageFactory — returns proper icon for all shell items
    /// including Store apps. Falls back to null if unavailable.
    /// </summary>
    private static MemoryStream? ExtractIconViaShellFactory(string filePath, int targetSize)
    {
        try
        {
            var hr = SHCreateItemFromParsingName(filePath, nint.Zero,
                typeof(IShellItem).GUID, out nint shellItemPtr);
            if (hr != 0 || shellItemPtr == nint.Zero)
            {
                Logger.Log("IconHelper",$"ShellFactory: SHCreateItemFromParsingName failed 0x{hr:X8}");
                return null;
            }

            try
            {
                var factory = (IShellItemImageFactory)Marshal.GetObjectForIUnknown(shellItemPtr);
                var size = new SIZE { cx = targetSize, cy = targetSize };
                hr = factory.GetImage(size, SIIGBF_BIGGERSIZEOK | SIIGBF_RESIZETOFIT | SIIGBF_SCALEUP, out nint hbitmap);

                if (hr != 0 || hbitmap == nint.Zero)
                {
                    Logger.Log("IconHelper",$"ShellFactory: GetImage failed 0x{hr:X8}");
                    return null;
                }

                try
                {
                    using var bitmap = System.Drawing.Image.FromHbitmap(hbitmap);
                    var ms = new MemoryStream();
                    bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    ms.Position = 0;
                    Logger.Log("IconHelper",$"ShellFactory OK: {filePath}");
                    return ms;
                }
                finally
                {
                    DeleteObject(hbitmap);
                }
            }
            finally
            {
                Marshal.Release(shellItemPtr);
            }
        }
        catch (Exception ex)
        {
            Logger.Log("IconHelper",$"ShellFactory error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Load an .ico file directly via System.Drawing.Icon constructor.
    /// SHGetFileInfo on .ico files returns the file-type icon (a palette), not the content.
    /// </summary>
    private static MemoryStream? ExtractIcoFile(string icoPath, int targetSize)
    {
        try
        {
            using var icon = new System.Drawing.Icon(icoPath);
            using var resized = new System.Drawing.Bitmap(targetSize, targetSize);
            using var g = System.Drawing.Graphics.FromImage(resized);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.Clear(System.Drawing.Color.Transparent);
            g.DrawIcon(icon, new System.Drawing.Rectangle(0, 0, targetSize, targetSize));
            var ms = new MemoryStream();
            resized.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;
            return ms;
        }
        catch (Exception ex)
        {
            Logger.Log("IconHelper", $"ExtractIcoFile error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Extract icon via the system image list (SHGetImageList SHIL_JUMBO = 256x256).
    /// Approach used by EverythingToolbar — gives the highest-quality shell icon.
    /// </summary>
    private static MemoryStream? ExtractIconViaImageList(string filePath, int targetSize)
    {
        try
        {
            var shfi = new SHFILEINFO();
            var ret = SHGetFileInfoW(filePath, 0, ref shfi, Marshal.SizeOf<SHFILEINFO>(), SHGFI_SYSICONINDEX);
            if (ret == nint.Zero || shfi.iIcon < 0) return null;

            var iid = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"); // IID_IImageList
            int hr = SHGetImageList(4 /*SHIL_JUMBO*/, ref iid, out var imgList);
            if (hr < 0 || imgList == null) return null;

            hr = imgList.GetIcon(shfi.iIcon, 0x1 /*ILD_TRANSPARENT*/, out nint hIcon);
            if (hr < 0 || hIcon == nint.Zero) return null;

            using var icon = System.Drawing.Icon.FromHandle(hIcon);
            using var resized = new System.Drawing.Bitmap(targetSize, targetSize);
            using var g = System.Drawing.Graphics.FromImage(resized);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.Clear(System.Drawing.Color.Transparent);
            g.DrawIcon(icon, new System.Drawing.Rectangle(0, 0, targetSize, targetSize));
            DestroyIcon(hIcon);

            var ms = new MemoryStream();
            resized.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;
            Logger.Log("IconHelper", $"ImageList OK: {filePath} size={targetSize}");
            return ms;
        }
        catch (Exception ex)
        {
            Logger.Log("IconHelper", $"ImageList error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Request icon at an exact target size via PrivateExtractIcons.
    /// Returns a PNG MemoryStream with the best-matching icon size.
    /// Works for .exe, .dll, .ico — requests up to 256x256.
    /// </summary>
    private static MemoryStream? ExtractIconViaPrivateExtract(string filePath, int targetSize)
    {
        try
        {
            uint count = PrivateExtractIcons(filePath, 0, targetSize, targetSize, out nint hIcon, out _, 1, 0);
            if (count <= 0 || hIcon == nint.Zero)
            {
                // Some exes (e.g. Electron) fail at larger sizes; retry at 32x32
                count = PrivateExtractIcons(filePath, 0, 32, 32, out hIcon, out _, 1, 0);
            }
            if (count <= 0 || hIcon == nint.Zero) return null;

            using var icon = System.Drawing.Icon.FromHandle(hIcon);
            using var resized = new System.Drawing.Bitmap(targetSize, targetSize);
            using var g = System.Drawing.Graphics.FromImage(resized);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.Clear(System.Drawing.Color.Transparent);
            g.DrawIcon(icon, new System.Drawing.Rectangle(0, 0, targetSize, targetSize));
            DestroyIcon(hIcon);

            var ms = new MemoryStream();
            resized.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;
            Logger.Log("IconHelper",$"PrivateExtractIcons OK: {filePath} size={targetSize}");
            return ms;
        }
        catch (Exception ex)
        {
            Logger.Log("IconHelper", $"PrivateExtractIcons error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Extract icon from exe and return PNG bytes in a MemoryStream.
    /// </summary>
    private static MemoryStream? ExtractIconToPngStream(string filePath, int targetSize)
    {
        if (string.IsNullOrEmpty(filePath) || (!File.Exists(filePath) && !Directory.Exists(filePath))) return null;

        var hIcon = SHGetFileInfo(filePath);
        if (hIcon == nint.Zero) return null;

        using var icon = System.Drawing.Icon.FromHandle(hIcon);

        // DrawIcon preserves alpha; ToBitmap() does not (black background)
        using var resized = new System.Drawing.Bitmap(targetSize, targetSize);
        using var g = System.Drawing.Graphics.FromImage(resized);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.Clear(System.Drawing.Color.Transparent);
        g.DrawIcon(icon, new System.Drawing.Rectangle(0, 0, targetSize, targetSize));
        DestroyIcon(hIcon);

        var ms = new MemoryStream();
        resized.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;
        return ms;
    }

    /// <summary>
    /// Last-resort icon extraction via ExtractIconEx API. Handles cases SHGetFileInfo misses.
    /// </summary>
    private static MemoryStream? ExtractIconExToPngStream(string filePath, int targetSize)
    {
        try
        {
            var count = ExtractIconEx(filePath, 0, out nint hIconLarge, out _, 1);
            if (count <= 0 || hIconLarge == nint.Zero)
            {
                Logger.Log("IconHelper",$"ExtractIconEx: no icon found for {filePath}");
                return null;
            }

            try
            {
                using var icon = System.Drawing.Icon.FromHandle(hIconLarge);
                using var resized = new System.Drawing.Bitmap(targetSize, targetSize);
                using var g = System.Drawing.Graphics.FromImage(resized);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.Clear(System.Drawing.Color.Transparent);
                g.DrawIcon(icon, new System.Drawing.Rectangle(0, 0, targetSize, targetSize));

                var ms = new MemoryStream();
                resized.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                ms.Position = 0;
                Logger.Log("IconHelper",$"ExtractIconEx OK: {filePath}");
                return ms;
            }
            finally
            {
                DestroyIcon(hIconLarge);
            }
        }
        catch (Exception ex)
        {
            Logger.Log("IconHelper",$"ExtractIconEx error: {ex.Message}");
            return null;
        }
    }

    private static nint SHGetFileInfo(string filePath, uint dwFileAttributes = 0)
    {
        var shfi = new SHFILEINFO();
        // FILE_ATTRIBUTE_DIRECTORY (0x10) is required for reliable directory icon retrieval.
        if (dwFileAttributes == 0 && !File.Exists(filePath) && Directory.Exists(filePath))
            dwFileAttributes = 0x10;
        var ret = SHGetFileInfoW(filePath, dwFileAttributes, ref shfi, Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON);
        return ret != nint.Zero ? shfi.hIcon : nint.Zero;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfoW(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, int cbFileInfo, uint uFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfoW(nint pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, int cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(nint hIcon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(string lpszFile, int nIconIndex, int cxIcon, int cyIcon, out nint phicon, out uint piconid, uint nIcons, uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHCreateItemFromParsingName(string pszPath, nint pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out nint ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string pszPath, nint pbc, out nint ppidl, uint sfgaoIn, ref uint psfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHCreateItemFromIDList(nint pidl, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out nint ppv);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(nint hObject);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int ExtractIconEx(string lpszFile, int nIconIndex, out nint phiconLarge, out nint phiconSmall, int nIcons);

    private const uint SHGFI_ICON = 0x00000100;
    private const uint SHGFI_LARGEICON = 0x00000000;
    private const uint SHGFI_PIDL = 0x00000008;
    
    private const uint SIIGBF_BIGGERSIZEOK = 0x00000001;
    private const uint SIIGBF_RESIZETOFIT = 0x00000002;
    private const uint SIIGBF_SCALEUP = 0x00000020;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public nint hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(nint pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid bhid, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out nint ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, uint flags, out nint phbm);
    }

    // ── IImageList (SHGetImageList / EverythingToolbar approach) ──

    [ComImport, Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageList
    {
        int Add(nint hbmImage, nint hbmMask, out int pi);
        int ReplaceIcon(int index, nint hicon, out int pi);
        int SetOverlayImage(int index, int overlay);
        int Replace(int index, nint hbmImage, nint hbmMask);
        int AddMasked(nint hbmImage, int crMask, out int pi);
        [PreserveSig] int Draw(nint pimldp);
        int Remove(int index);
        [PreserveSig] int GetIcon(int index, uint flags, out nint phicon);
        int GetImageInfo(int index, nint pImageInfo);
        int Copy(int iDst, nint punkSrc, int iSrc, uint uFlags);
        int Merge(int i1, nint punkMerge, int i2, int dx, int dy, ref Guid riid, out nint ppv);
        int Clone(ref Guid riid, out nint ppv);
        int GetImageRect(int index, nint prc);
        int GetIconSize(out int cx, out int cy);
        int SetIconSize(int cx, int cy);
        int GetImageCount(out int piCount);
        int SetImageCount(int uNewCount);
        int SetBegin();
        int End();
        int GetDragImage(nint ppt, nint pptHot, ref Guid riid, out nint ppv);
        int GetItemFlags(int index, out uint dwFlags);
        int GetOverlayImage(int overlay, out int piIndex);
    }

    private const uint SHGFI_SYSICONINDEX = 0x4000;

    [DllImport("shell32.dll")]
    private static extern int SHGetImageList(int iImageList, ref Guid riid, out IImageList ppv);
}
