using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
            if (!TrySetCachedIcon(vm, size))
                pendingIcons.Add(vm);
        }

        _items.Clear();
        foreach (var vm in viewModels)
            _items.Add(vm);
        IsLoading = false;

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
                    Aumid = a.Aumid
                }, Pinyin: ComputePinyin(a.Name)))
                .ToList();
            App.DispatcherQueue.TryEnqueue(() =>
            {
                _allAppsCache = cache;
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
            _items.Remove(item);

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

    /// <summary>If a cached icon exists on disk, set it synchronously. Returns true if set.</summary>
    private bool TrySetCachedIcon(LaunchpadItemViewModel vm, int size)
    {
        try
        {
            var cached = IconHelper.ExtractAppIconToAppData(vm.Model.AppPath, size, aumid: vm.Model.Aumid);
            if (cached == null) return false;
            var bitmap = new BitmapImage();
            bitmap.UriSource = new Uri(cached);
            vm.IconSource = bitmap;
            return true;
        }
        catch { return false; }
    }

    /// <summary>Extract uncached icons in background (throttled), then batch-apply in one UI frame.</summary>
    private async Task LoadIconsBatchAsync(List<LaunchpadItemViewModel> items, int size)
    {
        var extractTasks = items.Select(vm => Task.Run(async () =>
        {
            await _iconLoadThrottle.WaitAsync();
            try
            {
                IconHelper.ExtractAppIconToAppData(vm.Model.AppPath, size, aumid: vm.Model.Aumid);
            }
            finally { _iconLoadThrottle.Release(); }
        }));
        await Task.WhenAll(extractTasks);

        // All cached to disk now — apply in a single UI batch.
        App.DispatcherQueue.TryEnqueue(() =>
        {
            foreach (var vm in items)
            {
                try
                {
                    var tempFile = IconHelper.ExtractAppIconToAppData(vm.Model.AppPath, size, aumid: vm.Model.Aumid);
                    if (tempFile == null) continue;
                    var bitmap = new BitmapImage();
                    bitmap.UriSource = new Uri(tempFile);
                    vm.IconSource = bitmap;
                }
                catch { }
            }
        });
    }

    private static string ComputePinyin(string name)
    {
        var initials = PinyinHelper.GetInitials(name);
        var full = PinyinHelper.GetPinyin(name);
        if (string.IsNullOrEmpty(initials)) return full;
        if (string.Equals(initials, full, StringComparison.OrdinalIgnoreCase))
            return full;
        return $"{initials} {full}";
    }

    public void SaveItems()
    {
        var settings = _settingsService.Load();
        settings.LaunchpadItems = _items.Select(i => i.Model).ToList();
        _settingsService.Save(settings);
    }

    public void RemoveItem(LaunchpadItemViewModel item)
    {
        _items.Remove(item);
        SaveItems();
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ApplyFilter();
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
        // Search from user's items.
        var results = _items
            .Where(i => SearchHelper.FuzzyMatch(i.Name, query) || SearchHelper.FuzzyMatchPinyin(i.PinyinSearchData, query))
            .Select(i => (vm: i, isUnadded: false))
            .ToList();

        // Also search from all-apps cache for unadded items.
        var addedPaths = new HashSet<string>(_items
            .Where(i => !string.IsNullOrEmpty(i.Model.AppPath))
            .Select(i => i.Model.AppPath), StringComparer.OrdinalIgnoreCase);
        var addedAumids = new HashSet<string>(_items
            .Where(i => !string.IsNullOrEmpty(i.Model.Aumid))
            .Select(i => i.Model.Aumid), StringComparer.OrdinalIgnoreCase);

        if (_allAppsCache != null)
        {
            foreach (var (cachedItem, pinyin) in _allAppsCache)
            {
                if (!SearchHelper.FuzzyMatch(cachedItem.Name, query) && !SearchHelper.FuzzyMatchPinyin(pinyin, query))
                    continue;
                // Skip if already in user's items.
                if (!string.IsNullOrEmpty(cachedItem.AppPath) && addedPaths.Contains(cachedItem.AppPath))
                    continue;
                if (!string.IsNullOrEmpty(cachedItem.Aumid) && addedAumids.Contains(cachedItem.Aumid))
                    continue;
                var vm = new LaunchpadItemViewModel(cachedItem, pinyin) { IsUnadded = true };
                LoadSingleIcon(vm);
                results.Add((vm, isUnadded: true));
            }
        }

        // Sort: user's items first, then unadded cache items.
        return results.OrderBy(r => r.isUnadded).Select(r => r.vm).ToList();
    }

    /// <summary>Sync _filteredItems to match target list, avoiding CollectionReset
    /// (which resets GridView scroll position) when not searching.</summary>
    private void SyncToFiltered(List<LaunchpadItemViewModel> target)
    {
        if (string.IsNullOrWhiteSpace(_searchText))
        {
            SyncIncremental(target);
        }
        else
        {
            // Search active: full rebuild — user expects layout change, scroll reset is fine.
            _filteredItems.Clear();
            foreach (var item in target)
                _filteredItems.Add(item);
            // Notify so SelectFirstItem picks up the new results.
            OnPropertyChanged(nameof(FilteredItems));
        }
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
                await Task.Delay(150, token);
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
            XamlRoot = _xamlRootGetter?.Invoke() ?? _xamlRoot ?? App.Window.Content.XamlRoot
        };

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
                var tempFile = IconHelper.ExtractAppIconToAppData(vm.Model.AppPath, size, aumid: vm.Model.Aumid);
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
            var scale = xamlRoot?.RasterizationScale ?? 1.0;
            return (int)(baseSize * scale);
        }
        catch { return baseSize; }
    }
}

public class LaunchpadItemViewModel : ObservableObject
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

        if (_tool != null)
        {
            App.SystemThemeChanged += OnSystemThemeChanged;

            // Extract tool icon if the tool provides an extract path
            if (_tool.IconExtractPath is string extractPath && !string.IsNullOrEmpty(extractPath))
                _ = LoadToolIconAsync(extractPath);
        }
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
    public string ToolIconGlyph => _tool?.IconGlyph ?? "";
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

    private static string ComputePinyin(string name)
    {
        var initials = PinyinHelper.GetInitials(name);
        var full = PinyinHelper.GetPinyin(name);
        if (string.IsNullOrEmpty(initials)) return full;
        if (string.Equals(initials, full, StringComparison.OrdinalIgnoreCase))
            return full;
        return $"{initials} {full}";
    }

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
}
