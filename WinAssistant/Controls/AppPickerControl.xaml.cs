using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using WinAssistant.Helpers;
using WinAssistant.Models;
using WinAssistant.Services;

namespace WinAssistant.Controls;

public sealed partial class AppPickerControl : UserControl
{
    private readonly List<AppPickerItem> _pickerItems = [];
    private AppPickerViewModel _viewModel = null!;

    /// <summary>Fires when the user adds an app to the Launchpad.</summary>
    public event Action<LaunchpadItem>? ItemAdded;

    /// <summary>Fires when the user clicks the close button.</summary>
    public event Action? CloseRequested;

    private HashSet<string>? _existingPaths;

    public AppPickerControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        SearchBox.TextChanged += OnSearchChanged;
        RefreshButton.Click += OnRefresh;
        CloseButton.Click += (_, _) => CloseRequested?.Invoke();
    }

    /// <summary>
    /// Set paths already added, so they show as "已添加" in the list.
    /// Call before the control loads.
    /// </summary>
    public void SetExistingPaths(HashSet<string> paths) => _existingPaths = paths;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var apps = await Task.Run(() => AppScanner.ScanInstalledApps());
        BuildPickerItems(apps, _existingPaths ?? []);
        var iconSize = GetScaledIconSize(64);
        foreach (var item in _pickerItems)
            LoadIcon(item, iconSize);
    }

    private void BuildPickerItems(List<InstalledAppInfo> apps, HashSet<string> existingPaths)
    {
        _pickerItems.Clear();
        var items = apps
            .OrderByDescending(a => a.UsageCount)
            .ThenBy(a => a.Name)
            .Select(a =>
            {
                var added = !string.IsNullOrEmpty(a.AppPath) && existingPaths.Contains(a.AppPath)
                    || !string.IsNullOrEmpty(a.Aumid) && existingPaths.Contains("aumid::" + a.Aumid);
                var pi = new AppPickerItem(a.Name, a.AppPath, a.Arguments, a.Aumid, a.UsageCount, null, a.ShortcutPath, a.IconPath)
                {
                    IsAdded = added
                };
                pi.SetAddAction(OnItemAdd);
                return pi;
            })
            .ToList();

        _pickerItems.AddRange(items);
        _viewModel = new AppPickerViewModel(_pickerItems);
        AppListView.ItemsSource = _viewModel.FilteredApps;
    }

    private void OnItemAdd(AppPickerItem selected)
    {
        if (selected.IsAdded) return;
        selected.IsAdded = true;

        ItemAdded?.Invoke(new LaunchpadItem
        {
            Name = selected.Name,
            AppPath = selected.AppPath,
            Arguments = selected.Arguments,
            Aumid = selected.Aumid,
            IconPath = selected.IconPath != selected.AppPath ? selected.IconPath : null
        });
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _viewModel.Filter(SearchBox.Text);
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        try
        {
            var freshApps = await AppScanner.RefreshCacheAsync();
            var freshPaths = _pickerItems
                .Where(i => !string.IsNullOrEmpty(i.AppPath))
                .Select(i => i.AppPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            BuildPickerItems(freshApps, freshPaths);

            var iconSize = GetScaledIconSize(64);
            foreach (var item in _pickerItems)
                if (item.IconSource == null)
                    LoadIcon(item, iconSize);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private static void LoadIcon(AppPickerItem item, int targetSize)
    {
        if (string.IsNullOrEmpty(item.AppPath) && string.IsNullOrEmpty(item.Aumid)) return;
        _ = Task.Run(() =>
        {
            var tempFile = IconHelper.ExtractAppIconToAppData(item.IconPath ?? item.AppPath, targetSize, aumid: item.Aumid);
            if (tempFile == null) return;
            App.DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.UriSource = new Uri(tempFile);
                    item.IconSource = bitmap;
                }
                catch { }
            });
        });
    }

    private static int GetScaledIconSize(int baseSize) =>
        IconHelper.GetScaledIconSize(baseSize, App.WindowHandle);
}

/// <summary>
/// Lightweight ViewModel for the picker list filtering.
/// </summary>
internal class AppPickerViewModel
{
    private readonly List<AppPickerItem> _allApps;
    private readonly ObservableCollection<AppPickerItem> _filteredApps;

    public AppPickerViewModel(List<AppPickerItem> apps)
    {
        _allApps = apps;
        _filteredApps = new ObservableCollection<AppPickerItem>(apps);
    }

    public ObservableCollection<AppPickerItem> FilteredApps => _filteredApps;

    public void Filter(string searchText)
    {
        _filteredApps.Clear();
        var source = string.IsNullOrWhiteSpace(searchText)
            ? _allApps
            : _allApps.Where(a => SearchHelper.FuzzyMatch(a.Name, searchText)
                               || SearchHelper.FuzzyMatchPinyin(a.PinyinSearchData, searchText));
        foreach (var item in source)
            _filteredApps.Add(item);
    }
}
