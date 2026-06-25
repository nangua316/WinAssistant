using Microsoft.Windows.Storage.Pickers;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinAssistant.Controls.AiChat;
using WinAssistant.Controls.Tools;
using WinAssistant.Helpers;
using WinAssistant.ViewModels;

namespace WinAssistant.Pages;

public sealed partial class LaunchpadPage : Page
{
    private LaunchpadDragHandler? _dragHandler;

    public LaunchpadPageViewModel ViewModel { get; }

    public event EventHandler? CloseRequested;
    public event EventHandler<bool>? PinChanged;

    private bool _isPinned;
    public bool IsPinned
    {
        get => _isPinned;
        set { _isPinned = value; PinButton.IsChecked = value; }
    }

    public nint? OwnerHwnd { get; set; }

    public LaunchpadPage()
    {
        // 先设置主题再解析 XAML，确保 ThemeResource 用目标主题
        this.RequestedTheme = App.CurrentTheme == ApplicationTheme.Light
            ? ElementTheme.Light : ElementTheme.Dark;

        InitializeComponent();
        ViewModel = new LaunchpadPageViewModel();
        ViewModel.Items.CollectionChanged += OnItemsChanged;
        ViewModel.FilteredItems.CollectionChanged += (_, _) => UpdateEmptyState();
        ViewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(LaunchpadPageViewModel.SearchText))
                UpdateReorderState();
            if (e.PropertyName == nameof(LaunchpadPageViewModel.FilteredItems))
                SelectFirstItem();
        };
        SizeChanged += (_, _) => UpdateItemSize();
        AppGrid.Loaded += (_, _) => UpdateItemSize();
        // 注意：ItemContainerTransitions 在 XAML 中设置，不要在代码中覆盖为 null
        _dragHandler = new LaunchpadDragHandler(
            AppGrid, DragCanvas,
            ViewModel.Items,
            () => ViewModel.FilteredItems,
            () => !string.IsNullOrWhiteSpace(ViewModel.SearchText),
            () => ViewModel.SaveItems(),
            (Brush)Resources["ItemNameBrush"],
            (Brush)Resources["AccentBrush"]);
        UpdateReorderState();

        // 主题切换时更新
        App.SystemThemeChanged += OnSystemThemeChanged;
    }

    private void OnSystemThemeChanged(object? sender, EventArgs e)
    {
        this.RequestedTheme = App.CurrentTheme == ApplicationTheme.Light
            ? ElementTheme.Light : ElementTheme.Dark;

        // 更新 DragHandler 引用的内存 Brush
        _dragHandler?.UpdateBrushes(
            (Brush)Resources["ItemNameBrush"],
            (Brush)Resources["AccentBrush"]);
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateEmptyState();
    }

    private void SelectFirstItem()
    {
        if (!string.IsNullOrWhiteSpace(ViewModel.SearchText) && ViewModel.FilteredItems.Count > 0)
        {
            // Let GridView process collection change first, then select index 0.
            App.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => AppGrid.SelectedIndex = 0);
        }
    }

    public void Activate()
    {
        // 每次打开时刷新主题（防止关闭期间系统主题变化）
        this.RequestedTheme = App.CurrentTheme == ApplicationTheme.Light
            ? ElementTheme.Light : ElementTheme.Dark;

        var settings = App.SettingsService.Load();
        ViewModel.PreloadSearchText(settings.LastSearchText ?? "");
        ViewModel.LoadItems();
        ViewModel.SetXamlRoot(this.XamlRoot);
        SearchBox.Text = settings.LastSearchText ?? "";
        AppGrid.SelectedItem = null;
        UpdateItemSize();
        _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            UpdateItemSize();
            SearchBox.Focus(FocusState.Programmatic);
        });
    }

    private void UpdateItemSize()
    {
        var wrapGrid = FindWrapGrid();
        if (wrapGrid == null) return;
        var availableWidth = Math.Max(200, ActualWidth - 40);
        var itemWidth = Math.Max(72, (int)(availableWidth / 8));
        wrapGrid.ItemWidth = itemWidth;
        AppGrid.InvalidateMeasure();
        AppGrid.InvalidateArrange();
    }

    private ItemsWrapGrid? FindWrapGrid()
    {
        if (AppGrid.ItemsPanelRoot is ItemsWrapGrid wg) return wg;
        return FindVisualChild<ItemsWrapGrid>(AppGrid);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var result = FindVisualChild<T>(child);
            if (result != null) return result;
        }
        return null;
    }


    private void OnPinToggle(object sender, RoutedEventArgs e)
    {
        _isPinned = PinButton.IsChecked == true;
        PinChanged?.Invoke(this, _isPinned);
    }

    private void OnPageRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var point = e.GetPosition(AppGrid);
        var hitItem = _dragHandler?.FindItemAt(point);
        if (hitItem != null) return;

        var menu = new MenuFlyout();
        var addItem = new MenuFlyoutItem
        {
            Text = "添加应用",
            Icon = new FontIcon { Glyph = "" }
        };
        addItem.Click += (s, args) => ViewModel.AddAppCommand.Execute(null);
        menu.Items.Add(addItem);
        var folderItem = new MenuFlyoutItem
        {
            Text = "添加文件夹",
            Icon = new FontIcon { Glyph = "" }
        };
        folderItem.Click += OnAddFolderClick;
        menu.Items.Add(folderItem);
        var urlItem = new MenuFlyoutItem
        {
            Text = "添加网址",
            Icon = new FontIcon { Glyph = "" }
        };
        urlItem.Click += OnAddUrlClick;
        menu.Items.Add(urlItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        var settingsItem = new MenuFlyoutItem
        {
            Text = "打开设置",
            Icon = new FontIcon { Glyph = "" }
        };
        settingsItem.Click += (s, args) => App.DispatcherQueue.TryEnqueue(() =>
        {
            if (App.Window is MainWindow main) main.ShowSettings();
        });
        menu.Items.Add(settingsItem);
        menu.ShowAt((UIElement)sender, e.GetPosition((UIElement)sender));
        e.Handled = true;
    }

    private async void OnAddFolderClick(object? sender, RoutedEventArgs e)
    {
        // Suspend focus-lost close while picker is open
        PinChanged?.Invoke(this, true);
        SearchBox.IsEnabled = false; // prevent focus flash during picker transition

        try
        {
            var hwnd = OwnerHwnd ?? WinRT.Interop.WindowNative.GetWindowHandle(App.Window);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var folderPicker = new FolderPicker(windowId)
            {
                SuggestedStartLocation = PickerLocationId.Desktop,
                ViewMode = PickerViewMode.List,
                CommitButtonText = "选择文件夹"
            };

            var result = await folderPicker.PickSingleFolderAsync();
            if (result != null)
                ViewModel.AddFolderItem(result.Path, System.IO.Path.GetFileName(result.Path));
        }
        finally
        {
            SearchBox.IsEnabled = true;
            PinChanged?.Invoke(this, IsPinned);
        }
    }

    private async void OnAddUrlClick(object? sender, RoutedEventArgs e)
    {
        var urlBox = new TextBox
        {
            PlaceholderText = "例如：https://github.com",
            Header = "网址"
        };

        // Icon preview: shows the browser icon by default, replaced by the website favicon when fetched.
        var iconPreview = new Image
        {
            Width = 40,
            Height = 40,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        string? websiteFaviconPath = null;
        ImageSource? currentIconSource = null;

        var nameBox = new TextBox
        {
            PlaceholderText = "例如：GitHub",
            Header = "显示名称",
            VerticalAlignment = VerticalAlignment.Center
        };

        var iconNamePanel = new Grid
        {
            ColumnSpacing = 12
        };
        iconNamePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        iconNamePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(iconPreview, 0);
        Grid.SetColumn(nameBox, 1);
        iconNamePanel.Children.Add(iconPreview);
        iconNamePanel.Children.Add(nameBox);

        var fetchButton = new Button
        {
            Content = "获取图标和标题",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        // Browser picker: system default + installed browsers + manual override.
        var browserOptions = new ObservableCollection<BrowserScanner.BrowserInfo>();
        browserOptions.Add(new BrowserScanner.BrowserInfo("使用系统默认浏览器", "", BrowserScanner.GetDefaultBrowserIcon()));
        foreach (var browser in BrowserScanner.ScanInstalledBrowsers())
            browserOptions.Add(browser);

        var browserCombo = new ComboBox
        {
            Header = "浏览器",
            ItemsSource = browserOptions,
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>" +
                "<StackPanel Orientation='Horizontal' Spacing='8'>" +
                "<Image Source='{Binding IconSource}' Width='16' Height='16' VerticalAlignment='Center'/>" +
                "<TextBlock Text='{Binding Name}' VerticalAlignment='Center'/>" +
                "</StackPanel>" +
                "</DataTemplate>")
        };

        void UpdateIconPreview()
        {
            // Prefer website favicon if already fetched.
            if (currentIconSource != null)
            {
                iconPreview.Source = currentIconSource;
                return;
            }

            // Otherwise show the selected browser icon (or default browser icon).
            var selectedBrowser = browserCombo.SelectedItem as BrowserScanner.BrowserInfo;
            if (selectedBrowser?.IconSource != null)
                iconPreview.Source = selectedBrowser.IconSource;
        }

        browserCombo.SelectionChanged += (_, _) => UpdateIconPreview();

        var browseButton = new Button
        {
            Content = "浏览...",
            Margin = new Thickness(0, 4, 0, 0)
        };
        browseButton.Click += async (_, _) =>
        {
            var path = await PickBrowserExecutableAsync();
            if (string.IsNullOrEmpty(path)) return;

            var existing = browserOptions.FirstOrDefault(b => b.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                browserCombo.SelectedItem = existing;
                return;
            }

            var custom = new BrowserScanner.BrowserInfo(Path.GetFileNameWithoutExtension(path), path, BrowserScanner.LoadBrowserIcon(path));
            browserOptions.Insert(1, custom);
            browserCombo.SelectedItem = custom;
        };

        urlBox.LostFocus += async (_, _) =>
        {
            if (string.IsNullOrEmpty(nameBox.Text))
                await FetchWebsiteMetadataAsync();
        };

        fetchButton.Click += async (_, _) => await FetchWebsiteMetadataAsync();

        async Task FetchWebsiteMetadataAsync()
        {
            var url = urlBox.Text.Trim();
            if (string.IsNullOrEmpty(url)) return;

            var normalized = NormalizeUrl(url);
            if (!Uri.IsWellFormedUriString(normalized, UriKind.Absolute)) return;

            fetchButton.IsEnabled = false;
            fetchButton.Content = "获取中...";

            var info = await WebsiteMetadataHelper.FetchAsync(normalized);

            // Fallback to a real browser engine for sites that block plain HTTP requests (e.g. bilibili).
            if (info.FaviconSource == null && XamlRoot != null)
            {
                Debug.WriteLine($"Falling back to WebView2 for {normalized}");
                var webViewInfo = await WebView2FaviconHelper.FetchAsync(normalized, XamlRoot);
                if (webViewInfo != null)
                    info = webViewInfo;
            }

            if (!string.IsNullOrEmpty(info.Title))
                nameBox.Text = info.Title;

            if (info.FaviconSource != null)
            {
                websiteFaviconPath = info.FaviconPath;
                currentIconSource = info.FaviconSource;
            }
            else
            {
                websiteFaviconPath = null;
                currentIconSource = null;
            }

            UpdateIconPreview();

            fetchButton.IsEnabled = true;
            fetchButton.Content = "获取图标和标题";
        }

        var hint = new TextBlock
        {
            Text = "输入网址后点击“获取图标和标题”。如果网站图标获取失败，将使用浏览器图标。",
            FontSize = 12,
            Opacity = 0.7,
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(urlBox);
        panel.Children.Add(fetchButton);
        panel.Children.Add(iconNamePanel);
        panel.Children.Add(browserCombo);
        panel.Children.Add(browseButton);
        panel.Children.Add(hint);

        var dialog = new ContentDialog
        {
            Title = "添加网址",
            Content = panel,
            PrimaryButtonText = "添加",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };

        // Initialize preview with the default browser icon.
        UpdateIconPreview();

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        var name = nameBox.Text.Trim();
        var url = urlBox.Text.Trim();
        var browserPath = (browserCombo.SelectedItem as BrowserScanner.BrowserInfo)?.Path ?? "";

        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url))
        {
            await new ContentDialog
            {
                Title = "信息不完整",
                Content = "显示名称和网址不能为空。",
                CloseButtonText = "确定",
                XamlRoot = this.XamlRoot
            }.ShowAsync();
            return;
        }

        ViewModel.AddUrlItem(name, NormalizeUrl(url), browserPath, websiteFaviconPath);
    }


    private static string NormalizeUrl(string url)
    {
        url = url.Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            && !url.Contains("://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }
        return url;
    }

    private async Task<string?> PickBrowserExecutableAsync()
    {
        PinChanged?.Invoke(this, true);
        SearchBox.IsEnabled = false;
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop,
                ViewMode = Windows.Storage.Pickers.PickerViewMode.List
            };
            picker.FileTypeFilter.Add(".exe");

            var hwnd = OwnerHwnd ?? WinRT.Interop.WindowNative.GetWindowHandle(App.Window);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            return file?.Path;
        }
        finally
        {
            SearchBox.IsEnabled = true;
            PinChanged?.Invoke(this, IsPinned);
        }
    }

    private void OnPageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            // DEBUG: F5 切主题并打开设置（测试用）
            case Windows.System.VirtualKey.F5:
                ThemeSwitcherTool.ToggleTheme();
                (App.Window as MainWindow)?.ShowSettings();
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Escape:
                if (!string.IsNullOrEmpty(SearchBox.Text))
                {
                    SearchBox.Text = "";
                    e.Handled = true;
                    return;
                }
                Close();
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Enter:
                LaunchSelected();
                e.Handled = true;
                break;
        }
    }

    private void OnSearchBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Enter:
                LaunchSelected();
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Down:
                if (ViewModel.FilteredItems.Count > 0)
                {
                    if (AppGrid.SelectedItem == null)
                        AppGrid.SelectedIndex = 0;
                    AppGrid.Focus(FocusState.Programmatic);
                    e.Handled = true;
                }
                break;
            case Windows.System.VirtualKey.Up:
                if (AppGrid.SelectedItem != null)
                {
                    AppGrid.SelectedItem = null;
                    e.Handled = true;
                }
                break;
        }
    }

    private void OnAppGridKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Enter:
                LaunchSelected();
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Escape:
                Close();
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Up:
                if (AppGrid.SelectedIndex <= 0)
                {
                    AppGrid.SelectedItem = null;
                    SearchBox.Focus(FocusState.Programmatic);
                    e.Handled = true;
                }
                break;
        }
    }

    private void LaunchSelected()
    {
        var vm = AppGrid.SelectedItem as LaunchpadItemViewModel
            ?? (ViewModel.FilteredItems.Count > 0 ? ViewModel.FilteredItems[0] : null);
        if (vm != null)
            LaunchOrClose(vm);
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is LaunchpadItemViewModel vm)
            LaunchOrClose(vm);
    }

    private async void LaunchOrClose(LaunchpadItemViewModel vm)
    {
        if (HandleToolClick(vm)) return;
        if (vm.IsUninstalled) { await ShowUninstalledDialog(vm); return; }

        Close(clearSearch: true);
        var itemName = vm.Name;

        if (vm.IsUrl)
        {
            var url = vm.Model.Url;
            var browserPath = vm.Model.BrowserPath;
            _ = Task.Run(() =>
            {
                var action = LaunchUrl(url, browserPath);
                App.DispatcherQueue.TryEnqueue(() =>
                    ShowLaunchToast(action, itemName, url));
            });
            return;
        }

        var path = vm.AppPath;
        var args = vm.Model.Arguments;
        var aumid = vm.Model.Aumid;
        _ = Task.Run(() =>
        {
            var action = AppLauncher.LaunchOrActivate(path, args, aumid);
            App.DispatcherQueue.TryEnqueue(() =>
                ShowLaunchToast(action, itemName, path));
        });
    }

    /// <summary>Launch a URL using the specified browser, or the system default browser.</summary>
    private static string LaunchUrl(string url, string browserPath)
    {
        try
        {
            if (!string.IsNullOrEmpty(browserPath) && File.Exists(browserPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = browserPath,
                    Arguments = url,
                    UseShellExecute = true
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            return "launch";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to launch URL: {ex.Message}");
        }
        return "";
    }

    /// <summary>Show dialog for uninstalled app, offer to remove the item.</summary>
    private async Task ShowUninstalledDialog(LaunchpadItemViewModel vm)
    {
        var dialog = new ContentDialog
        {
            Title = "应用已卸载",
            Content = $"\"{vm.Name}\" 已从电脑中移除，无法启动。\n是否从启动台删除此图标？",
            PrimaryButtonText = "删除图标",
            CloseButtonText = "保留",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            ViewModel.RemoveItem(vm);
        }
    }

    /// <summary>Handles tool item clicks. Returns true if the item is a tool (handled or window-opened).</summary>
    private bool HandleToolClick(LaunchpadItemViewModel vm)
    {
        if (!vm.IsTool || vm.Tool == null) return false;

        Close(clearSearch: true);

        if (vm.Tool.IsOneClickAction)
        {
            var msg = vm.Tool.Activate();
            if (!string.IsNullOrEmpty(msg))
            {
                try
                {
                    var iconPath = vm.Tool.IconExtractPath;
                    if (string.IsNullOrEmpty(iconPath))
                        iconPath = Process.GetCurrentProcess().MainModule?.FileName;
                    HotKeyToast.Show(msg, iconPath: iconPath);
                }
                catch { }
            }
            return true;
        }
        ToolHostWindow.OpenOrActivate(vm.Tool);
        return true;
    }

    private async void OnRemoveItem(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item &&
            item.DataContext is LaunchpadItemViewModel vm)
        {
            if (vm.IsUnadded)
            {
                // Add the unadded app to the user's launchpad.
                ViewModel.AddUnaddedItem(vm);
                return;
            }
            var dialog = new ContentDialog
            {
                Title = "移除应用",
                Content = $"确定要从启动台移除 \"{vm.Name}\"？",
                PrimaryButtonText = "移除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                ViewModel.RemoveItem(vm);
        }
    }


    private void UpdateReorderState()
    {
        // Always enabled — drag/reorder is disabled, no reason to block clicks.
        AppGrid.IsItemClickEnabled = true;
    }

    private void UpdateEmptyState()
    {
        var hasItems = ViewModel.HasItems;
        var hasFilterResults = ViewModel.FilteredItems.Count > 0;
        EmptyStatePanel.Visibility = !hasItems ? Visibility.Visible : Visibility.Collapsed;
        NoResultsPanel.Visibility = hasItems && !hasFilterResults ? Visibility.Visible : Visibility.Collapsed;
    }

    private static ChatWindow? _chatWindow;

    private void OnAiChatClick(object sender, RoutedEventArgs e)
    {
        if (_chatWindow == null)
        {
            _chatWindow = new ChatWindow();
            _chatWindow.Closed += (_, _) => _chatWindow = null;
        }
        _chatWindow.Activate();
    }

    private void Close(bool clearSearch = false)
    {
        // persist search text before closing (unless app was launched)
        var settings = App.SettingsService.Load();
        settings.LastSearchText = clearSearch ? "" : ViewModel.SearchText;
        App.SettingsService.Save(settings);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private static void ShowLaunchToast(string action, string appName, string? iconPath = null)
    {
        if (string.IsNullOrEmpty(action)) return;
        var verb = action switch
        {
            "minimize" => "最小化",
            "launch" => "打开",
            _ => "激活"
        };
        try { HotKeyToast.Show(verb, appName, iconPath); }
        catch { }
    }
}
