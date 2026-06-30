using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;
using WinAssistant.Controls.Tools;
using WinAssistant.Helpers;
using WinAssistant.Models;
using WinAssistant.Services;

namespace WinAssistant.ViewModels;

public class LaunchpadPageViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly ObservableCollection<LaunchpadItemViewModel> _items = [];
    private ObservableCollection<LaunchpadItemViewModel> _filteredItems = [];
    private bool _suppressCollectionChanged;
    private string _searchText = "";
    private CancellationTokenSource? _searchCts;
    private List<(LaunchpadItem Item, string Pinyin)>? _allAppsCache;
    private Microsoft.UI.Xaml.XamlRoot? _xamlRoot;
    private Func<Microsoft.UI.Xaml.XamlRoot?>? _xamlRootGetter;

    public LaunchpadPageViewModel()
    {
        _settingsService = App.SettingsService;
        AddAppCommand = new AsyncRelayCommand(AddAppAsync);
        _items.CollectionChanged += OnItemsCollectionChanged;
    }

    /// <summary>True while LoadItems is running, suppresses auto-save.</summary>
    public bool IsLoading { get; private set; }

    public void SetXamlRoot(Microsoft.UI.Xaml.XamlRoot root) => _xamlRoot = root;
    public void SetXamlRootGetter(Func<Microsoft.UI.Xaml.XamlRoot?> getter) => _xamlRootGetter = getter;

    public ObservableCollection<LaunchpadItemViewModel> Items => _items;

    public ObservableCollection<LaunchpadItemViewModel> FilteredItems
    {
        get => _filteredItems;
        set => SetProperty(ref _filteredItems, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
                DebounceApplyFilter();
        }
    }

    /// <summary>Set search text without triggering PropertyChanged or filter.
    /// Used before LoadItems to avoid async binding delay causing visual jumps.</summary>
    public void PreloadSearchText(string text) => _searchText = text;

    public ICommand AddAppCommand { get; }

    private bool _itemsLoaded;

    public void LoadItems()
    {
        if (_itemsLoaded)
        {
            // Sync tool items from settings (user may have toggled tools on/off
            // in the settings page since the last launchpad open).
            IsLoading = true;
            SyncToolItemsFromSettings();
            ApplyFilter();
            IsLoading = false;
            return;
        }
        _itemsLoaded = true;
        IsLoading = true;
        var settings = _settingsService.Load();
        var viewModels = settings.LaunchpadItems.Select(m => new LaunchpadItemViewModel(m)).ToList();

        // Pre-set cached icons synchronously BEFORE adding to grid,
        // so items render with icons on first frame (no fallback→icon pop-in).
        var size = GetScaledIconSize(64);
        var pendingIcons = new List<LaunchpadItemViewModel>();
        foreach (var vm in viewModels)
        {
            if (!TrySetCachedIcon(vm, size) && !vm.ShowFontIconGlyph)
                pendingIcons.Add(vm);
        }

        // Dispose old items, then clear
        foreach (var vm in _items) vm.Dispose();
        _items.Clear();
        foreach (var vm in viewModels)
            _items.Add(vm);
        IsLoading = false;

        // Initial filter (was suppressed during bulk load by IsLoading flag).
        ApplyFilter();

        // Load remaining (uncached) icons in background.
        if (pendingIcons.Count > 0)
            _ = LoadIconsBatchAsync(pendingIcons, size);

        // Load all installed apps in background for search-as-you-type.
        _ = Task.Run(() =>
        {
            var apps = AppScanner.ScanInstalledApps();
            var cache = apps
                .Where(a => !string.IsNullOrWhiteSpace(a.Name))
                .Select(a => (Item: new LaunchpadItem
                {
                    Name = a.Name,
                    AppPath = a.AppPath,
                    Arguments = a.Arguments,
                    Aumid = a.Aumid,
                    IconPath = a.IconPath != a.AppPath ? a.IconPath : null
                }, Pinyin: ComputePinyin(a.Name)))
                .ToList();
            App.DispatcherQueue.TryEnqueue(() =>
            {
                _allAppsCache = cache;

                // Update existing items' IconPath from cache (auto-fix icons for
                // Electron apps etc. where the AppPath exe doesn't carry the real icon).
                var cacheByPath = cache
                    .Where(c => !string.IsNullOrEmpty(c.Item.IconPath))
                    .ToDictionary(c => c.Item.AppPath, c => c.Item.IconPath, StringComparer.OrdinalIgnoreCase);
                foreach (var itemVm in _items)
                {
                    if (itemVm.Model.IconPath != null) continue; // already has a custom icon
                    if (!string.IsNullOrEmpty(itemVm.Model.AppPath)
                        && cacheByPath.TryGetValue(itemVm.Model.AppPath, out var cacheIconPath))
                    {
                        itemVm.Model.IconPath = cacheIconPath;
                        // Reload icon from the corrected path.
                        LoadSingleIcon(itemVm);
                    }
                }

                // Re-run filter now that cache is ready so unadded items appear.
                if (!string.IsNullOrWhiteSpace(_searchText))
                    ApplyFilter();
            });
        });
    }

    /// <summary>
    /// Sync tool items (ToolId != null) from settings into the in-memory _items,
    /// so toggling a tool on/off in settings is reflected the next time the
    /// launchpad opens, without a full reload.
    /// </summary>
    private void SyncToolItemsFromSettings()
    {
        var settings = _settingsService.Load();
        var settingsToolItems = settings.LaunchpadItems
            .Where(i => i.ToolId != null)
            .ToList();
        var settingsToolIds = settingsToolItems
            .Select(i => i.ToolId)
            .ToHashSet();

        // Remove tools that were toggled off in settings.
        var toRemove = _items
            .Where(i => i.Model.ToolId != null && !settingsToolIds.Contains(i.Model.ToolId))
            .ToList();
        foreach (var item in toRemove)
        {
            _items.Remove(item);
            item.Dispose();
        }

        // Add tools that were toggled on in settings.
        var currentToolIds = _items
            .Where(i => i.Model.ToolId != null)
            .Select(i => i.Model.ToolId)
            .ToHashSet();
        foreach (var toolItem in settingsToolItems)
        {
            if (!currentToolIds.Contains(toolItem.ToolId))
                _items.Add(new LaunchpadItemViewModel(toolItem));
        }
    }

    /// <summary>Fixed path for the terminal icon, shared by all script items.</summary>
    private static string GetTerminalIconPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WinAssistant", "terminal-icon.png");

    private static string IconExtractPath(LaunchpadItem m) =>
        !string.IsNullOrWhiteSpace(m.FontIconGlyph) ? "" : // glyph icon — no extraction needed
        m.IconPath ?? (!string.IsNullOrWhiteSpace(m.BrowserPath) ? m.BrowserPath :
            !string.IsNullOrWhiteSpace(m.AppPath) ? m.AppPath :
            !string.IsNullOrWhiteSpace(m.Script) && File.Exists(GetTerminalIconPath()) ? GetTerminalIconPath() :
            !string.IsNullOrWhiteSpace(m.Script) ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"WindowsPowerShell\v1.0\powershell.exe") :
            "");

    private bool TrySetCachedIcon(LaunchpadItemViewModel vm, int size)
    {
        try
        {
            // Website favicons are already image files; load them directly.
            var iconPath = vm.Model.IconPath;
            if (!string.IsNullOrEmpty(iconPath) && IsImageFile(iconPath))
            {
                Logger.Log("TrySetCachedIcon", $"{vm.Name}: direct icon={iconPath}");
                var bitmap = new BitmapImage();
                bitmap.UriSource = new Uri(iconPath);
                vm.IconSource = bitmap;
                return true;
            }

            var extractPath = IconExtractPath(vm.Model);
            Logger.Log("TrySetCachedIcon", $"{vm.Name}: extract from={extractPath ?? "null"}");
            if (string.IsNullOrEmpty(extractPath)) return false;

            var cached = IconHelper.ExtractAppIconToAppData(extractPath, size, aumid: vm.Model.Aumid);
            if (cached == null) return false;
            var bitmap2 = new BitmapImage();
            bitmap2.UriSource = new Uri(cached);
            vm.IconSource = bitmap2;
            return true;
        }
        catch { return false; }
    }

    private static bool IsImageFile(string path) =>
        path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase);

    /// <summary>Extract uncached icons in background (throttled), then batch-apply in one UI frame.</summary>
    private async Task LoadIconsBatchAsync(List<LaunchpadItemViewModel> items, int size)
    {
        // 第一次提取：写入磁盘缓存，同时记录返回的缓存路径
        var cacheResults = new Dictionary<LaunchpadItemViewModel, string?>();
        var extractTasks = items.Select(vm => Task.Run(async () =>
        {
            await _iconLoadThrottle.WaitAsync();
            try
            {
                var cachedPath = IconHelper.ExtractAppIconToAppData(IconExtractPath(vm.Model), size, aumid: vm.Model.Aumid);
                lock (cacheResults) { cacheResults[vm] = cachedPath; }
            }
            finally { _iconLoadThrottle.Release(); }
        }));
        await Task.WhenAll(extractTasks);

        // 所有图标已缓存到磁盘 — 直接使用缓存路径，不再二次提取
        App.DispatcherQueue.TryEnqueue(() =>
        {
            foreach (var vm in items)
            {
                try
                {
                    var tempFile = cacheResults.GetValueOrDefault(vm);
                    if (tempFile == null) continue;
                    var bitmap = new BitmapImage();
                    bitmap.UriSource = new Uri(tempFile);
                    vm.IconSource = bitmap;
                }
                catch { }
            }
        });
    }

    private static string ComputePinyin(string name) => PinyinHelper.GetSearchData(name);

    public void SaveItems()
    {
        var settings = _settingsService.Load();
        settings.LaunchpadItems = _items.Select(vm => vm.Model).ToList();
        _settingsService.Save(settings);
    }

    /// <summary>Load icon for a newly added item immediately (not waiting for restart).</summary>
    public void LoadItemIcon(LaunchpadItemViewModel vm)
    {
        TrySetCachedIcon(vm, GetScaledIconSize(64));
    }

    public void RemoveItem(LaunchpadItemViewModel item)
    {
        _items.Remove(item);
        item.Dispose();
        SaveItems();
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (IsLoading || _suppressCollectionChanged) return;
        ApplyFilter();
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(StatusText));
    }

    /// <summary>Swap the positions of two items in the launchpad. Used by drag-to-swap reordering.</summary>
    public void SwapItems(LaunchpadItemViewModel a, LaunchpadItemViewModel b)
    {
        var aIdx = _items.IndexOf(a);
        var bIdx = _items.IndexOf(b);
        if (aIdx < 0 || bIdx < 0 || aIdx == bIdx) return;

        var faIdx = _filteredItems.IndexOf(a);
        var fbIdx = _filteredItems.IndexOf(b);
        if (faIdx < 0 || fbIdx < 0) return;

        _suppressCollectionChanged = true;
        if (aIdx < bIdx)
        {
            _items.Move(aIdx, bIdx);
            _items.Move(bIdx - 1, aIdx);
        }
        else
        {
            _items.Move(aIdx, bIdx);
            _items.Move(bIdx + 1, aIdx);
        }
        _suppressCollectionChanged = false;

        if (faIdx < fbIdx)
        {
            _filteredItems.Move(faIdx, fbIdx);
            _filteredItems.Move(fbIdx - 1, faIdx);
        }
        else
        {
            _filteredItems.Move(faIdx, fbIdx);
            _filteredItems.Move(fbIdx + 1, faIdx);
        }

        SaveItems();
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(StatusText));
    }

    /// <summary>Move an item to the end of the launchpad. Used when dropping an icon in trailing empty space.</summary>
    public void MoveItemToEnd(LaunchpadItemViewModel item)
    {
        var idx = _items.IndexOf(item);
        if (idx < 0 || idx == _items.Count - 1) return;

        var fIdx = _filteredItems.IndexOf(item);
        if (fIdx < 0) return;

        _suppressCollectionChanged = true;
        _items.Move(idx, _items.Count - 1);
        _suppressCollectionChanged = false;

        _filteredItems.Move(fIdx, _filteredItems.Count - 1);

        SaveItems();
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(StatusText));
    }

    public bool HasItems => _items.Count > 0;
    public string StatusText => $"共 {_items.Count} 个应用 · Enter 启动 · ↑↓ 导航 · Esc 关闭";

    private void ApplyFilter()
    {
        var items = BuildFilteredList();
        SyncToFiltered(items);
    }

    private List<LaunchpadItemViewModel> BuildFilteredList()
    {
        if (string.IsNullOrWhiteSpace(_searchText))
            return [.. _items];

        var query = _searchText.Trim();

        // Single-pass: compute score once per item for both filtering and sorting.
        var scored = new List<(LaunchpadItemViewModel vm, int score, bool isUnadded)>();

        foreach (var item in _items)
        {
            int score = GetMatchScore(item.Name, item.PinyinSearchData, query);
            if (score > 0)
                scored.Add((item, score, false));
        }

        // Also search from all-apps cache for unadded items.
        var addedPaths = new HashSet<string>(_items
            .Where(i => !string.IsNullOrEmpty(i.Model.AppPath))
            .Select(i => i.Model.AppPath), StringComparer.OrdinalIgnoreCase);
        var addedAumids = new HashSet<string>(_items
            .Where(i => !string.IsNullOrEmpty(i.Model.Aumid))
            .Select(i => i.Model.Aumid), StringComparer.OrdinalIgnoreCase);

        if (_allAppsCache != null)
        {
            int unaddedCount = 0;
            const int maxUnaddedResults = 15;
            foreach (var (cachedItem, pinyin) in _allAppsCache)
            {
                int score = GetMatchScore(cachedItem.Name, pinyin, query);
                if (score <= 0) continue;
                // Skip if already in user's items.
                if (!string.IsNullOrEmpty(cachedItem.AppPath) && addedPaths.Contains(cachedItem.AppPath))
                    continue;
                if (!string.IsNullOrEmpty(cachedItem.Aumid) && addedAumids.Contains(cachedItem.Aumid))
                    continue;
                if (++unaddedCount > maxUnaddedResults) break;
                var vm = new LaunchpadItemViewModel(cachedItem, pinyin) { IsUnadded = true };
                LoadSingleIcon(vm);
                scored.Add((vm, score, isUnadded: true));
            }
        }

        // Sort by combined display rank: exact name match items first regardless
        // of added status, then user items by score, then unadded by score.
        return scored
            .OrderBy(r => GetDisplayRank(r.score, r.isUnadded))
            .Select(r => r.vm)
            .ToList();
    }

    /// <summary>
    /// Calculate match relevance score. Higher = better match.
    /// Name matches always beat pinyin matches; exact prefix/substring beats fuzzy sequential.
    /// </summary>
    private static int GetMatchScore(string name, string? pinyinData, string query)
    {
        // 1. Name matching (highest weight)
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 1000;
        if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 500;

        // Sequential fuzzy match on name
        int ti = 0;
        bool nameFuzzy = false;
        foreach (var qc in query)
        {
            while (ti < name.Length && char.ToLowerInvariant(name[ti]) != char.ToLowerInvariant(qc))
                ti++;
            if (ti >= name.Length) { nameFuzzy = false; break; }
            nameFuzzy = true;
            ti++;
        }
        if (nameFuzzy) return 200;

        // 2. Pinyin matching
        if (!string.IsNullOrEmpty(pinyinData))
        {
            var fullPinyin = pinyinData.Replace(" ", "");

            // Full pinyin contains query as substring (e.g. "weixin" in "wx wei xin")
            if (fullPinyin.Contains(query, StringComparison.OrdinalIgnoreCase))
                return 100;

            // Check first segment (initials, e.g. "wx")
            var segments = pinyinData.Split(' ');
            if (segments.Length > 0 && segments[0].Contains(query, StringComparison.OrdinalIgnoreCase))
                return 60;

            // Sequential fuzzy on concatenated pinyin
            ti = 0;
            bool pinyinFuzzy = false;
            foreach (var qc in query)
            {
                while (ti < fullPinyin.Length && char.ToLowerInvariant(fullPinyin[ti]) != char.ToLowerInvariant(qc))
                    ti++;
                if (ti >= fullPinyin.Length) { pinyinFuzzy = false; break; }
                pinyinFuzzy = true;
                ti++;
            }
            if (pinyinFuzzy) return 30;

            // Slowest: sequential fuzzy on individual segments
            foreach (var seg in segments)
            {
                if (string.IsNullOrEmpty(seg)) continue;
                int si = 0;
                bool segFuzzy = false;
                foreach (var qc in query)
                {
                    while (si < seg.Length && char.ToLowerInvariant(seg[si]) != char.ToLowerInvariant(qc))
                        si++;
                    if (si >= seg.Length) { segFuzzy = false; break; }
                    segFuzzy = true;
                    si++;
                }
                if (segFuzzy) return 15;
            }
        }

        return 0;
    }

    /// <summary>
    /// Combined display rank: lower = shown first.
    /// Takes the already-computed score + isUnadded to avoid redundant work.
    /// Items with exact/substring name match (score >= 500) get top priority
    /// regardless of whether they're in the user's launchpad, so a highly
    /// relevant unadded item beats a weakly-matching user item.
    /// </summary>
    private static int GetDisplayRank(int score, bool isUnadded)
    {
        // Top tier: exact/substring name match — always first
        if (score >= 500)
            return 500 - score; // -500..0; higher score = lower rank

        // Middle tier: user items with fuzzy/pinyin match
        if (!isUnadded)
            return 10000 + (500 - score); // 10001..10500; higher score = lower rank

        // Bottom tier: unadded items with fuzzy/pinyin match
        return 20000 + (500 - score); // 20001..20500; higher score = lower rank
    }

    /// <summary>Sync _filteredItems to match target list, avoiding CollectionReset
    /// (which resets GridView scroll position) when not searching.</summary>
    private void SyncToFiltered(List<LaunchpadItemViewModel> target)
    {
        // Always use incremental sync to preserve RepositionThemeTransition animations
        // — Clear()+Add triggers abrupt flash for all items.
        var hadItems = _filteredItems.Count > 0;
        SyncIncremental(target);
        if (!hadItems)
            OnPropertyChanged(nameof(FilteredItems)); // initial load: notify for SelectFirstItem
    }

    /// <summary>In-place sync: remove, insert, reorder — no Clear(), preserves scroll.</summary>
    private void SyncIncremental(List<LaunchpadItemViewModel> target)
    {
        // Remove items no longer in the target.
        for (int i = _filteredItems.Count - 1; i >= 0; i--)
        {
            if (!target.Contains(_filteredItems[i]))
                _filteredItems.RemoveAt(i);
        }

        // Align order and insert any missing items.
        int fi = 0;
        for (int ti = 0; ti < target.Count; ti++)
        {
            if (fi >= _filteredItems.Count)
            {
                _filteredItems.Add(target[ti]);
                fi++;
            }
            else if (ReferenceEquals(_filteredItems[fi], target[ti]))
            {
                fi++;
            }
            else
            {
                // Look for target[ti] later in _filteredItems.
                int foundAt = -1;
                for (int j = fi + 1; j < _filteredItems.Count; j++)
                {
                    if (ReferenceEquals(_filteredItems[j], target[ti]))
                    { foundAt = j; break; }
                }
                if (foundAt >= 0)
                {
                    var item = _filteredItems[foundAt];
                    _filteredItems.RemoveAt(foundAt);
                    _filteredItems.Insert(fi, item);
                    fi++;
                }
                else
                {
                    _filteredItems.Insert(fi, target[ti]);
                    fi++;
                }
            }
        }
    }

    /// <summary>
    /// Add a previously unadded cache item (user clicked to add it).
    /// </summary>
    internal void AddUnaddedItem(LaunchpadItemViewModel vm)
    {
        if (!vm.IsUnadded) return;
        vm.IsUnadded = false;
        _items.Add(vm);
        SaveItems();
        LoadSingleIcon(vm);
    }


    private void DebounceApplyFilter()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(200, token);
                token.ThrowIfCancellationRequested();
                App.DispatcherQueue.TryEnqueue(() =>
                {
                    if (token.IsCancellationRequested) return;
                    ApplyFilter();
                });
            }
            catch (OperationCanceledException) { }
        });
    }

    private async Task AddAppAsync()
    {
        var existingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in _items)
        {
            if (!string.IsNullOrEmpty(i.Model.AppPath))
                existingPaths.Add(i.Model.AppPath);
            if (!string.IsNullOrEmpty(i.Model.Aumid))
                existingPaths.Add("aumid::" + i.Model.Aumid);
        }

        var picker = new Controls.AppPickerControl();
        picker.SetExistingPaths(existingPaths);

        var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            Title = "选择应用程序",
            Content = picker,
            XamlRoot = _xamlRootGetter?.Invoke() ?? _xamlRoot ?? App.Window.Content.XamlRoot,
            RequestedTheme = Microsoft.UI.Xaml.ElementTheme.Default
        };
        // Match the app's current theme explicitly (ContentDialog doesn't always
        // inherit Application.RequestedTheme changes at runtime on some WinUI builds).
        var appTheme = App.GetSystemTheme();
        dialog.RequestedTheme = appTheme == ApplicationTheme.Light
            ? ElementTheme.Light
            : ElementTheme.Dark;

        picker.ItemAdded += item =>
        {
            // Deduplicate name if needed.
            var finalName = item.Name;
            int suffix = 2;
            while (_items.Any(i => i.Name.Equals(finalName, StringComparison.OrdinalIgnoreCase)))
                finalName = $"{item.Name} ({suffix++})";
            item.Name = finalName;

            var vm = new LaunchpadItemViewModel(item);
            _items.Add(vm);
            SaveItems();
            LoadSingleIcon(vm);
        };

        picker.CloseRequested += () => dialog.Hide();

        await dialog.ShowAsync();
    }

    internal void AddFolderItem(string folderPath, string folderName)
    {
        var finalName = folderName;
        int suffix = 2;
        while (_items.Any(i => i.Name.Equals(finalName, StringComparison.OrdinalIgnoreCase)))
            finalName = $"{folderName} ({suffix++})";

        var item = new LaunchpadItem
        {
            Name = finalName,
            AppPath = folderPath
        };
        var vm = new LaunchpadItemViewModel(item);
        _items.Add(vm);
        SaveItems();
        LoadSingleIcon(vm);
    }

    internal void AddFileItem(string filePath, string fileName)
    {
        var finalName = fileName;
        int suffix = 2;
        while (_items.Any(i => i.Name.Equals(finalName, StringComparison.OrdinalIgnoreCase)))
            finalName = $"{fileName} ({suffix++})";

        var item = new LaunchpadItem
        {
            Name = finalName,
            AppPath = filePath
        };
        var vm = new LaunchpadItemViewModel(item);
        _items.Add(vm);
        SaveItems();
        LoadSingleIcon(vm);
    }

    internal void AddUrlItem(string name, string url, string browserPath, string? websiteFaviconPath = null)
    {
        var finalName = name;
        int suffix = 2;
        while (_items.Any(i => i.Name.Equals(finalName, StringComparison.OrdinalIgnoreCase)))
            finalName = $"{name} ({suffix++})";

        var item = new LaunchpadItem
        {
            Name = finalName,
            Url = url,
            BrowserPath = browserPath
        };

        // Cache the icon so the URL item looks like a real app.
        // Priority: website favicon > specific browser icon > system default browser icon.
        if (!string.IsNullOrEmpty(websiteFaviconPath) && File.Exists(websiteFaviconPath))
        {
            item.IconPath = websiteFaviconPath;
            Logger.Log("LaunchpadPageViewModel", $"AddUrlItem using website favicon={websiteFaviconPath}");
        }
        else
        {
            var iconSourcePath = !string.IsNullOrEmpty(browserPath) && File.Exists(browserPath)
                ? browserPath
                : BrowserScanner.FindDefaultBrowserPath();
            Logger.Log("LaunchpadPageViewModel", $"AddUrlItem iconSourcePath={iconSourcePath}");
            if (!string.IsNullOrEmpty(iconSourcePath) && File.Exists(iconSourcePath))
            {
                var cachedIcon = IconHelper.ExtractAppIconToAppData(iconSourcePath, GetScaledIconSize(64));
                Logger.Log("LaunchpadPageViewModel", $"AddUrlItem cachedIcon={cachedIcon}");
                if (cachedIcon != null)
                    item.IconPath = cachedIcon;
            }
        }

        var vm = new LaunchpadItemViewModel(item);
        _items.Add(vm);
        SaveItems();
        LoadSingleIcon(vm);
    }

    private static readonly SemaphoreSlim _iconLoadThrottle = new(3, 3);

    /// <summary>Load a single item's icon: try cache first, fall back to async extraction.</summary>
    private void LoadSingleIcon(LaunchpadItemViewModel vm)
    {
        var size = GetScaledIconSize(64);
        if (TrySetCachedIcon(vm, size)) return;

        _ = Task.Run(async () =>
        {
            await _iconLoadThrottle.WaitAsync();
            try
            {
                var tempFile = IconHelper.ExtractAppIconToAppData(IconExtractPath(vm.Model), size, aumid: vm.Model.Aumid);
                if (tempFile == null) return;
                App.DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        var bitmap = new BitmapImage();
                        bitmap.UriSource = new Uri(tempFile);
                        vm.IconSource = bitmap;
                    }
                    catch { }
                });
            }
            finally { _iconLoadThrottle.Release(); }
        });
    }

    private int GetScaledIconSize(int baseSize)
    {
        try
        {
            var xamlRoot = _xamlRootGetter?.Invoke() ?? _xamlRoot;
            var scale = xamlRoot?.RasterizationScale ?? 2.0;
            return Math.Max(baseSize, (int)(baseSize * scale));
        }
        catch { return baseSize * 2; }
    }
}

public class LaunchpadItemViewModel : ObservableObject, IDisposable
{
    private ImageSource? _iconSource;
    private readonly IAssistantTool? _tool;

    public LaunchpadItem Model { get; }
    public LaunchpadItemViewModel(LaunchpadItem model) : this(model, null) { }

    internal LaunchpadItemViewModel(LaunchpadItem model, string? precomputedPinyin)
    {
        Model = model;
        _tool = model.ToolId != null ? ToolRegistry.Get(model.ToolId) : null;
        _pinyinSearchData = precomputedPinyin ?? ComputePinyin(model.Name);
        IsUninstalled = CheckUninstalled(model);

        if (_tool != null)
        {
            App.SystemThemeChanged += OnSystemThemeChanged;

            // Extract tool icon if the tool provides an extract path
            if (_tool.IconExtractPath is string extractPath && !string.IsNullOrEmpty(extractPath))
                _ = LoadToolIconAsync(extractPath);
        }
    }

    public void Dispose()
    {
        if (_tool != null)
            App.SystemThemeChanged -= OnSystemThemeChanged;
    }

    private async Task LoadToolIconAsync(string extractPath)
    {
        var tempFile = await Task.Run(() =>
            IconHelper.ExtractAppIconToAppData(extractPath, 64));
        if (tempFile == null) return;

        App.DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.UriSource = new Uri(tempFile);
                IconSource = bitmap;
            }
            catch { }
        });
    }

    private void OnSystemThemeChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(ToolIconGlyph));
        OnPropertyChanged(nameof(ToolIconBrush));
    }

    public string Name => _tool?.Name ?? Model.Name;
    public string AppPath => Model.AppPath;
    public bool IsTool => _tool != null;
    public bool IsUrl => !string.IsNullOrWhiteSpace(Model.Url);
    public string ToolIconGlyph => _tool?.IconGlyph ?? "";
    public string FontIconGlyph => Model.FontIconGlyph ?? "";
    public bool ShowFontIconGlyph => !string.IsNullOrEmpty(Model.FontIconGlyph) && !IsTool;
    /// <summary>Only show fallback circle+letter when there's no icon at all.</summary>
    public bool ShowFallback => !HasIcon && !ShowFontIconGlyph;
    public IAssistantTool? Tool => _tool;
    public string FallbackChar => _tool != null ? "" : (Name.Length > 0 ? Name[..1] : "?");

    private bool _isUnadded;
    /// <summary>True when this item comes from the all-apps cache and is not yet added.</summary>
    public bool IsUnadded
    {
        get => _isUnadded;
        set
        {
            if (SetProperty(ref _isUnadded, value))
                OnPropertyChanged(nameof(ContextMenuText));
        }
    }

    public string ContextMenuText => IsUnadded ? "添加" : "移除";

    /// <summary>Whether this item is a URL shortcut (only URL items show the edit button).</summary>
    public bool IsUrlItem => !string.IsNullOrEmpty(Model.Url);

    /// <summary>Whether this item is a PowerShell script.</summary>
    public bool IsScriptItem => !string.IsNullOrEmpty(Model.Script);

    /// <summary>Whether this item is editable (URLs and scripts).</summary>
    public bool IsEditable => IsUrlItem || IsScriptItem;

    /// <summary>Tool icon foreground brush. Falls back to accent blue.</summary>
    public Brush ToolIconBrush
    {
        get
        {
            if (_tool?.IconColorHex is string hex)
            {
                try
                {
                    hex = hex.TrimStart('#');
                    var a = byte.Parse(hex[..2], System.Globalization.NumberStyles.HexNumber);
                    var r = byte.Parse(hex[2..4], System.Globalization.NumberStyles.HexNumber);
                    var g = byte.Parse(hex[4..6], System.Globalization.NumberStyles.HexNumber);
                    var b = byte.Parse(hex[6..8], System.Globalization.NumberStyles.HexNumber);
                    return new SolidColorBrush(Color.FromArgb(a, r, g, b));
                }
                catch { }
            }
            return new SolidColorBrush(Color.FromArgb(0xFF, 0x60, 0xA5, 0xFA));
        }
    }

    // Pinyin search data: initials + full pinyin, for matching against user input
    private readonly string _pinyinSearchData;
    public string PinyinSearchData => _pinyinSearchData;

    private static string ComputePinyin(string name) => PinyinHelper.GetSearchData(name);

    public ImageSource? IconSource
    {
        get => _iconSource;
        set
        {
            if (SetProperty(ref _iconSource, value))
            {
                OnPropertyChanged(nameof(HasIcon));
                OnPropertyChanged(nameof(ShowToolGlyph));
            }
        }
    }

    public bool HasIcon => _iconSource != null;
    public bool ShowToolGlyph => IsTool && _iconSource == null;

    /// <summary>True when this app's executable no longer exists on disk (uninstalled).
    /// Cached at construction — File.Exists is expensive to call per template instantiation.</summary>
    public bool IsUninstalled { get; }

    private static bool CheckUninstalled(LaunchpadItem model)
    {
        if (!string.IsNullOrWhiteSpace(model.Url)) return false;
        if (model.ToolId != null) return false;
        var path = model.AppPath;
        if (string.IsNullOrEmpty(path)) return false;
        return !File.Exists(path) && !Directory.Exists(path);
    }

    private bool _isBeingDragged;
    public bool IsBeingDragged
    {
        get => _isBeingDragged;
        set => SetProperty(ref _isBeingDragged, value);
    }

    private bool _isDragOverTarget;
    public bool IsDragOverTarget
    {
        get => _isDragOverTarget;
        set => SetProperty(ref _isDragOverTarget, value);
    }

    /// <summary>Call after Model.FontIconGlyph/IconPath changes to refresh the bound display properties.</summary>
    public void RefreshIconDisplay()
    {
        if (!string.IsNullOrEmpty(Model.FontIconGlyph))
        {
            // Switching to glyph — clear any file-based icon
            if (_iconSource != null)
                IconSource = null;
        }
        else if (!string.IsNullOrEmpty(Model.IconPath) && File.Exists(Model.IconPath))
        {
            // Switching back to file icon (e.g. "无图标" → terminal icon)
            try
            {
                var bitmap = new BitmapImage { UriSource = new Uri(Model.IconPath) };
                IconSource = bitmap;
            }
            catch { IconSource = null; }
        }
        else
            IconSource = null;

        OnPropertyChanged(nameof(FontIconGlyph));
        OnPropertyChanged(nameof(ShowFontIconGlyph));
        OnPropertyChanged(nameof(ShowFallback));
    }
}
