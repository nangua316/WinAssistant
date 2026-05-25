using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
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

    public ICommand AddAppCommand { get; }

    public void LoadItems()
    {
        IsLoading = true;
        var settings = _settingsService.Load();
        var viewModels = settings.LaunchpadItems.Select(m => new LaunchpadItemViewModel(m)).ToList();

        _items.Clear();
        foreach (var vm in viewModels)
            _items.Add(vm);

        foreach (var vm in _items)
            PreloadIcon(vm);
        IsLoading = false;

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
        if (string.IsNullOrWhiteSpace(_searchText))
        {
            FilteredItems = _items;
        }
        else
        {
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
                    var iconSize = GetScaledIconSize(64);
                    LoadIconAsync(vm.Model.AppPath, vm.Model.Aumid, iconSize, icon => vm.IconSource = icon);
                    results.Add((vm, isUnadded: true));
                }
            }

            // Sort: user's items first, then unadded cache items.
            results = results.OrderBy(r => r.isUnadded).ToList();
            FilteredItems = new ObservableCollection<LaunchpadItemViewModel>(results.Select(r => r.vm));
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
        PreloadIcon(vm);
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
            PreloadIcon(vm);
        };

        picker.CloseRequested += () => dialog.Hide();

        await dialog.ShowAsync();
    }

    private void PreloadIcon(LaunchpadItemViewModel vm)
    {
        var size = GetScaledIconSize(64);
        LoadIconAsync(vm.Model.AppPath, vm.Model.Aumid, size, icon => vm.IconSource = icon);
    }

    private static void LoadIconAsync(string appPath, string aumid, int targetSize, Action<ImageSource> onLoaded)
    {
        _ = Task.Run(() =>
        {
            var tempFile = IconHelper.ExtractAppIconToAppData(appPath, targetSize, aumid: aumid);
            if (tempFile == null) return;
            App.DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.UriSource = new Uri(tempFile);
                    onLoaded(bitmap);
                }
                catch { }
            });
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

    public LaunchpadItem Model { get; }
    public LaunchpadItemViewModel(LaunchpadItem model) : this(model, null) { }

    internal LaunchpadItemViewModel(LaunchpadItem model, string? precomputedPinyin)
    {
        Model = model;
        _pinyinSearchData = precomputedPinyin ?? ComputePinyin(model.Name);
    }

    public string Name => Model.Name;
    public string AppPath => Model.AppPath;
    public string FallbackChar => Name.Length > 0 ? Name[..1] : "?";

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
        set => SetProperty(ref _iconSource, value);
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
}
