using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media.Imaging;
using WinAssistant.Helpers;
using WinAssistant.Models;
using WinAssistant.Services;

namespace WinAssistant.ViewModels;

public class MainPageViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly HotKeyService _hotKeyService;
    private ObservableCollection<HotKeyBindingViewModel> _bindings = [];
    private string _statusMessage = "";
    private bool _isAutoStart;
    private bool _isLaunchpadEnabled;
    private string _launchpadTrigger = "DoubleCtrl";
    private List<InstalledAppInfo>? _cachedApps;
    private Task<List<InstalledAppInfo>>? _preloadTask;

    public MainPageViewModel(SettingsService settingsService, HotKeyService hotKeyService)
    {
        _settingsService = settingsService;
        _hotKeyService = hotKeyService;

        AddApplicationCommand = new AsyncRelayCommand(AddApplicationAsync);
        RemoveBindingCommand = new AsyncRelayCommand<HotKeyBindingViewModel?>(RemoveBindingAsync);
        SetHotKeyCommand = new AsyncRelayCommand<HotKeyBindingViewModel?>(SetHotKeyAsync);
        ToggleBindingCommand = new RelayCommand<HotKeyBindingViewModel?>(ToggleBinding);
    }

    public ObservableCollection<HotKeyBindingViewModel> Bindings
    {
        get => _bindings;
        set
        {
            if (SetProperty(ref _bindings, value))
            {
                if (_bindings != null)
                    _bindings.CollectionChanged += (_, _) => UpdateEmptyState();
                UpdateEmptyState();
            }
        }
    }

    public Microsoft.UI.Xaml.Visibility EmptyStateVisibility { get; private set; }

    private void UpdateEmptyState()
    {
        EmptyStateVisibility = _bindings.Count == 0
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;
        OnPropertyChanged(nameof(EmptyStateVisibility));
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>
    /// Whether the app should auto-start when Windows boots.
    /// </summary>
    public bool IsAutoStart
    {
        get => _isAutoStart;
        set
        {
            if (SetProperty(ref _isAutoStart, value))
            {
                SetAutoStartRegistry(value);
                SaveSettings();
            }
        }
    }

    public bool IsLaunchpadEnabled
    {
        get => _isLaunchpadEnabled;
        set
        {
            if (SetProperty(ref _isLaunchpadEnabled, value))
            {
                OnPropertyChanged(nameof(LaunchpadSettingsVisibility));
                UpdateDoubleKeyDetector();
                SaveSettings();
            }
        }
    }

    public Microsoft.UI.Xaml.Visibility LaunchpadSettingsVisibility =>
        _isLaunchpadEnabled ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public string LaunchpadTrigger
    {
        get => _launchpadTrigger;
        set
        {
            if (SetProperty(ref _launchpadTrigger, value))
            {
                UpdateDoubleKeyDetector();
                SaveSettings();
            }
        }
    }

    public ICommand AddApplicationCommand { get; }
    public ICommand RemoveBindingCommand { get; }
    public ICommand SetHotKeyCommand { get; }
    public ICommand ToggleBindingCommand { get; }

    public void LoadSettings()
    {
        var settings = _settingsService.Load();
        Bindings = new ObservableCollection<HotKeyBindingViewModel>(
            settings.Bindings.Select(b => new HotKeyBindingViewModel(b))
        );
        _isAutoStart = settings.IsAutoStart;
        OnPropertyChanged(nameof(IsAutoStart));
        _isLaunchpadEnabled = settings.IsLaunchpadEnabled;
        OnPropertyChanged(nameof(IsLaunchpadEnabled));
        _launchpadTrigger = string.IsNullOrEmpty(settings.LaunchpadTrigger) ? "DoubleCtrl" : settings.LaunchpadTrigger;
        OnPropertyChanged(nameof(LaunchpadTrigger));
        UpdateDoubleKeyDetector();

        RefreshHotKeys();
        StatusMessage = $"已加载 {Bindings.Count} 个快捷键绑定";

        // Preload icons for saved bindings
        foreach (var vm in Bindings)
            PreloadIcon(vm);

        // Preload app list in background
        _preloadTask = Task.Run(() =>
        {
            var apps = AppScanner.ScanInstalledApps();
            _cachedApps = apps;
            return apps;
        });
    }

    public void SaveSettings()
    {
        var current = _settingsService.Load();
        current.IsAutoStart = _isAutoStart;
        current.IsLaunchpadEnabled = _isLaunchpadEnabled;
        current.LaunchpadTrigger = _launchpadTrigger;
        current.Bindings = Bindings.Select(b => b.Model).ToList();
        _settingsService.Save(current);
    }

    public void RefreshHotKeys()
    {
        _hotKeyService.Refresh(Bindings.Select(b => b.Model).ToList());
    }

    public void HandleHotKeyPressed(HotKeyBinding binding)
    {
        var action = AppLauncher.LaunchOrActivate(binding.AppPath, binding.Arguments, binding.Aumid);
        if (!string.IsNullOrEmpty(action))
        {
            var verb = action switch
            {
                "minimize" => "最小化",
                _ => "激活"
            };
            try { HotKeyToast.Show($"{verb} {binding.Name}"); }
            catch { }
        }
    }

    private async Task AddApplicationAsync()
    {
        // Use cached apps for instant opening (preloaded at startup or from last picker open)
        var apps = _cachedApps ?? await (_preloadTask ??= Task.Run(() => AppScanner.ScanInstalledApps()));
        _cachedApps = apps;

        if (apps.Count == 0)
        {
            StatusMessage = "未找到已安装的应用程序";
            return;
        }

        // Build add callback for each picker item
        void AddItem(AppPickerItem selected)
        {
            var name = selected.Name;
            var finalName = name;
            int suffix = 2;
            while (Bindings.Any(b => b.Name.Equals(finalName, StringComparison.OrdinalIgnoreCase)))
                finalName = $"{name} ({suffix++})";

            var binding = new HotKeyBinding
            {
                Name = finalName,
                AppPath = selected.AppPath,
                Arguments = selected.Arguments,
                ShortcutPath = selected.ShortcutPath,
                WorkingDirectory = Path.GetDirectoryName(selected.AppPath) ?? "",
                Aumid = selected.Aumid
            };

            // Auto-detect Chromium browser profile: if no arguments set, read the
            // last-used profile so it opens directly instead of showing a profile picker.
            if (string.IsNullOrEmpty(binding.Arguments))
            {
                var exeName = Path.GetFileName(binding.AppPath);
                var profileArg = DetectBrowserProfileArg(exeName);
                if (profileArg != null)
                    binding.Arguments = profileArg;
            }

            Bindings.Add(new HotKeyBindingViewModel(binding));
            SaveSettings();
            StatusMessage = $"已添加: {finalName}";
            PreloadIcon(Bindings[^1]);
        }

        // Convert to AppPickerItem for observable display with icons
        var existingPaths = new HashSet<string>(Bindings
            .Where(b => !string.IsNullOrEmpty(b.AppPath))
            .Select(b => b.AppPath), StringComparer.OrdinalIgnoreCase);
        var existingAumids = new HashSet<string>(Bindings
            .Where(b => !string.IsNullOrEmpty(b.Model.Aumid))
            .Select(b => b.Model.Aumid), StringComparer.OrdinalIgnoreCase);

        var pickerItems = apps
            .OrderByDescending(a => a.UsageCount)
            .ThenBy(a => a.Name)
            .Select(a =>
            {
                var item = new AppPickerItem(a.Name, a.AppPath, a.Arguments, a.Aumid, a.UsageCount, AddItem, a.ShortcutPath);
                if (!string.IsNullOrEmpty(a.AppPath) && existingPaths.Contains(a.AppPath))
                    item.IsAdded = true;
                else if (!string.IsNullOrEmpty(a.Aumid) && existingAumids.Contains(a.Aumid))
                    item.IsAdded = true;
                return item;
            })
            .ToList();
        var viewModel = new AppPickerViewModel(pickerItems);

        // Build dialog content via code to avoid XAML compilation issues
        var grid = new Microsoft.UI.Xaml.Controls.Grid
        {
            Width = 480,
            RowDefinitions =
            {
                new Microsoft.UI.Xaml.Controls.RowDefinition { Height = Microsoft.UI.Xaml.GridLength.Auto },
                new Microsoft.UI.Xaml.Controls.RowDefinition { Height = new Microsoft.UI.Xaml.GridLength(1, Microsoft.UI.Xaml.GridUnitType.Star) },
                new Microsoft.UI.Xaml.Controls.RowDefinition { Height = Microsoft.UI.Xaml.GridLength.Auto }
            }
        };

        var searchBox = new Microsoft.UI.Xaml.Controls.TextBox
        {
            PlaceholderText = "搜索应用程序...",
            Margin = new Microsoft.UI.Xaml.Thickness(0, 0, 0, 8)
        };
        Microsoft.UI.Xaml.Controls.Grid.SetRow(searchBox, 0);
        grid.Children.Add(searchBox);

        var listView = new Microsoft.UI.Xaml.Controls.ListView
        {
            ItemsSource = viewModel.FilteredApps,
            SelectionMode = Microsoft.UI.Xaml.Controls.ListViewSelectionMode.None,
            MinHeight = 300,
            MaxHeight = 420
        };

        listView.ItemTemplate = (Microsoft.UI.Xaml.DataTemplate)
            Microsoft.UI.Xaml.Application.Current.Resources["AppPickerTemplate"];

        Microsoft.UI.Xaml.Controls.Grid.SetRow(listView, 1);
        grid.Children.Add(listView);

        searchBox.TextChanged += (s, e) =>
        {
            viewModel.Filter(searchBox.Text);
        };

        // Load icons on background, set on UI thread
        _ = Task.Run(() =>
        {
            foreach (var item in pickerItems)
            {
                if (string.IsNullOrEmpty(item.AppPath) && string.IsNullOrEmpty(item.Aumid)) continue;
                var tempFile = IconHelper.ExtractAppIconToAppData(item.AppPath, aumid: item.Aumid);
                if (tempFile == null) continue;
                App.DispatcherQueue.TryEnqueue(() =>
                {
                    var bitmap = new BitmapImage();
                    bitmap.UriSource = new Uri(tempFile);
                    item.IconSource = bitmap;
                });
            }
        });

        // Row 2: Bottom bar with custom add + close buttons
        var bottomBar = new Microsoft.UI.Xaml.Controls.Grid
        {
            Margin = new Microsoft.UI.Xaml.Thickness(0, 8, 0, 0),
            ColumnDefinitions =
            {
                new Microsoft.UI.Xaml.Controls.ColumnDefinition { Width = Microsoft.UI.Xaml.GridLength.Auto },
                new Microsoft.UI.Xaml.Controls.ColumnDefinition { Width = Microsoft.UI.Xaml.GridLength.Auto },
                new Microsoft.UI.Xaml.Controls.ColumnDefinition { Width = new Microsoft.UI.Xaml.GridLength(1, Microsoft.UI.Xaml.GridUnitType.Star) },
                new Microsoft.UI.Xaml.Controls.ColumnDefinition { Width = Microsoft.UI.Xaml.GridLength.Auto }
            }
        };

        var customAddButton = new Microsoft.UI.Xaml.Controls.Button
        {
            Content = "自定义添加应用",
            MinWidth = 140,
            MinHeight = 36,
            Padding = new Microsoft.UI.Xaml.Thickness(14, 6, 14, 6),
            CornerRadius = new Microsoft.UI.Xaml.CornerRadius(6),
            FontSize = 14
        };

        Microsoft.UI.Xaml.Controls.ContentDialog dialog = null!;

        customAddButton.Click += async (s, e2) =>
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                ViewMode = Windows.Storage.Pickers.PickerViewMode.List,
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder
            };
            picker.FileTypeFilter.Add(".exe");
            picker.FileTypeFilter.Add(".lnk");
            picker.FileTypeFilter.Add(".bat");
            picker.FileTypeFilter.Add(".cmd");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.Window);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                dialog.Hide();
                var name = System.IO.Path.GetFileNameWithoutExtension(file.Name);
                var finalName = name;
                int suffix = 2;
                while (Bindings.Any(b => b.Name.Equals(finalName, StringComparison.OrdinalIgnoreCase)))
                    finalName = $"{name} ({suffix++})";

                var binding = new HotKeyBinding
                {
                    Name = finalName,
                    AppPath = file.Path,
                    Arguments = "",
                    WorkingDirectory = System.IO.Path.GetDirectoryName(file.Path) ?? "",
                    Aumid = ""
                };

                Bindings.Add(new HotKeyBindingViewModel(binding));
                SaveSettings();
                StatusMessage = $"已添加: {finalName}";
                PreloadIcon(Bindings[^1]);
            }
        };

        Microsoft.UI.Xaml.Controls.Grid.SetColumn(customAddButton, 0);
        bottomBar.Children.Add(customAddButton);

        var helpText = new Microsoft.UI.Xaml.Controls.TextBlock
        {
            Text = "?",
            FontSize = 16,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray),
            Margin = new Microsoft.UI.Xaml.Thickness(4, 0, 0, 0),
            IsTabStop = true
        };
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(helpText,
            "若无法搜到程序，可手动添加应用程序地址到此处");
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(helpText, 1);
        bottomBar.Children.Add(helpText);

        var closeButton = new Microsoft.UI.Xaml.Controls.Button
        {
            Content = "关闭",
            MinWidth = 80,
            MinHeight = 36,
            Padding = new Microsoft.UI.Xaml.Thickness(16, 6, 16, 6),
            CornerRadius = new Microsoft.UI.Xaml.CornerRadius(6),
            FontSize = 14
        };
        closeButton.Click += (s, e2) => dialog.Hide();

        Microsoft.UI.Xaml.Controls.Grid.SetColumn(closeButton, 3);
        bottomBar.Children.Add(closeButton);

        Microsoft.UI.Xaml.Controls.Grid.SetRow(bottomBar, 2);
        grid.Children.Add(bottomBar);

        dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            Title = "选择应用程序",
            Content = grid,
            XamlRoot = App.Window.Content.XamlRoot
        };

        await dialog.ShowAsync();
    }

    private static void PreloadIcon(HotKeyBindingViewModel vm)
    {
        _ = Task.Run(() =>
        {
            var tempFile = IconHelper.ExtractAppIconToAppData(vm.Model.AppPath, aumid: vm.Model.Aumid);
            if (tempFile == null) return;
            App.DispatcherQueue.TryEnqueue(() =>
            {
                var bitmap = new BitmapImage();
                bitmap.UriSource = new Uri(tempFile);
                vm.IconSource = bitmap;
            });
        });
    }

    private async Task RemoveBindingAsync(HotKeyBindingViewModel? vm)
    {
        if (vm == null) return;

        var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            Title = "确认删除",
            Content = $"确定要移除 \"{vm.Name}\" 的快捷键绑定吗？",
            PrimaryButtonText = "确认",
            CloseButtonText = "取消",
            DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Close,
            XamlRoot = App.Window.Content.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
            return;

        if (vm.Model.HotKeyId > 0)
            _hotKeyService.Unregister(vm.Model.HotKeyId);
        Bindings.Remove(vm);
        SaveSettings();
        StatusMessage = $"已移除: {vm.Name}";
    }

    private async Task SetHotKeyAsync(HotKeyBindingViewModel? vm)
    {
        if (vm == null) return;

        uint capturedMods = 0;
        uint capturedVk = 0;
        var capturedDisplay = "";

        var inputBox = new Microsoft.UI.Xaml.Controls.TextBox
        {
            Text = "按下快捷键组合...",
            FontSize = 28,
            TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center,
            IsReadOnly = true,
            Width = 320,
            Height = 60,
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
            Margin = new Microsoft.UI.Xaml.Thickness(0, 30, 0, 30)
        };

        var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            Title = "设置快捷键",
            Content = inputBox,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Primary,
            XamlRoot = App.Window.Content.XamlRoot
        };

        inputBox.KeyDown += (s, e) =>
        {
            var key = e.Key;

            uint mods = 0;
            if (IsKeyDown(Windows.System.VirtualKey.Control)) mods |= KeyHelper.MOD_CONTROL;
            if (IsKeyDown(Windows.System.VirtualKey.Menu)) mods |= KeyHelper.MOD_ALT;
            if (IsKeyDown(Windows.System.VirtualKey.Shift)) mods |= KeyHelper.MOD_SHIFT;
            if (IsKeyDown(Windows.System.VirtualKey.LeftWindows) ||
                IsKeyDown(Windows.System.VirtualKey.RightWindows))
                mods |= KeyHelper.MOD_WIN;

            bool isMod = key is Windows.System.VirtualKey.Control
                or Windows.System.VirtualKey.Menu
                or Windows.System.VirtualKey.Shift
                or Windows.System.VirtualKey.LeftWindows
                or Windows.System.VirtualKey.RightWindows;

            if (isMod)
            {
                inputBox.Text = mods > 0
                    ? $"{KeyHelper.GetModifierDisplay(mods)} + ..."
                    : "按下快捷键组合...";
                e.Handled = true;
                return;
            }

            if (mods == 0)
            {
                inputBox.Text = "请至少包含一个修饰键 (Ctrl/Alt/Shift/Win)";
                e.Handled = true;
                return;
            }

            capturedMods = mods;
            capturedVk = (uint)key;
            capturedDisplay = KeyHelper.GetFullDisplay(mods, (uint)key);
            inputBox.Text = capturedDisplay;
            e.Handled = true;
        };

        var result = await dialog.ShowAsync();
        var confirmed = result == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary;

        if (confirmed && capturedMods > 0 && capturedVk > 0)
        {
            if (vm.Model.HotKeyId > 0)
                _hotKeyService.Unregister(vm.Model.HotKeyId);

            // Check conflicts with other bindings
            var conflict = FindBindingConflict(capturedMods, capturedVk, vm);
            if (conflict != null)
            {
                await ShowConflictDialogAsync($"快捷键 {capturedDisplay} 已被 \"{conflict.Name}\" 使用");
                return;
            }

            vm.Model.Modifiers = capturedMods;
            vm.Model.VirtualKey = capturedVk;
            vm.Model.HotKeyDisplay = capturedDisplay;
            vm.NotifyHotKeyChanged();

            if (vm.Model.IsEnabled)
            {
                var id = _hotKeyService.Register(vm.Model);
                if (id < 0)
                {
                    await ShowConflictDialogAsync($"快捷键 {capturedDisplay} 注册失败，可能被其他程序占用");
                    return;
                }
            }

            SaveSettings();
            StatusMessage = $"快捷键 {capturedDisplay} 已绑定到 {vm.Name}";
        }
    }

    private static bool IsKeyDown(Windows.System.VirtualKey key)
    {
        return Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
    }

    private void ToggleBinding(HotKeyBindingViewModel? vm)
    {
        if (vm == null) return;

        // With OneWay binding, IsEnabled still holds the old state — flip it
        vm.IsEnabled = !vm.IsEnabled;

        if (vm.IsEnabled && vm.Model.Modifiers != 0 && vm.Model.VirtualKey != 0)
        {
            var id = _hotKeyService.Register(vm.Model);
            if (id < 0)
            {
                vm.IsEnabled = false;
                _ = ShowConflictDialogAsync($"快捷键 {vm.Model.HotKeyDisplay} 注册失败，可能被其他程序占用");
                return;
            }
        }
        else if (!vm.IsEnabled && vm.Model.HotKeyId > 0)
            _hotKeyService.Unregister(vm.Model.HotKeyId);

        SaveSettings();
    }

    /// <summary>
    /// Show a conflict/warning dialog.
    /// </summary>
    private async Task ShowConflictDialogAsync(string message)
    {
        try
        {
            var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
            {
                Title = "快捷键冲突",
                Content = message,
                CloseButtonText = "确定",
                XamlRoot = App.Window.Content.XamlRoot
            };
            await dialog.ShowAsync();
        }
        catch { }
    }

    private void UpdateDoubleKeyDetector()
    {
        if (!_isLaunchpadEnabled)
        {
            App.DoubleKeyDetector.Stop();
            App.WinKeyInterceptor.Stop();
            return;
        }

        if (_launchpadTrigger == "SingleWin")
        {
            App.DoubleKeyDetector.Stop();
            App.WinKeyInterceptor.Start();
        }
        else
        {
            App.WinKeyInterceptor.Stop();
            var vk = _launchpadTrigger switch
            {
                "DoubleAlt" => Windows.System.VirtualKey.Menu,
                "DoubleShift" => Windows.System.VirtualKey.Shift,
                "DoubleWin" => Windows.System.VirtualKey.LeftWindows,
                _ => Windows.System.VirtualKey.Control
            };
            App.DoubleKeyDetector.Start(vk);
        }
    }

    /// <summary>
    /// Check if any other binding in the list already uses this key combination.
    /// </summary>
    internal HotKeyBindingViewModel? FindBindingConflict(uint modifiers, uint virtualKey, HotKeyBindingViewModel? exclude)
    {
        return Bindings.FirstOrDefault(b =>
            b != exclude &&
            b.Model.Modifiers == modifiers &&
            b.Model.VirtualKey == virtualKey);
    }

    /// <summary>
    /// Read the last-used profile from a Chromium-based browser's Local State
    /// and return the --profile-directory arg. Supports Chrome, Edge, Brave, Chromium.
    /// </summary>
    private static string? DetectBrowserProfileArg(string exeName)
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["chrome.exe"] = @"Google\Chrome\User Data",
            ["msedge.exe"] = @"Microsoft\Edge\User Data",
            ["brave.exe"] = @"BraveSoftware\Brave-Browser\User Data",
            ["chromium.exe"] = @"Chromium\User Data",
        };

        if (!paths.TryGetValue(exeName, out var relativePath))
            return null;

        var localState = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            relativePath, "Local State");

        if (!File.Exists(localState)) return null;

        try
        {
            var json = File.ReadAllText(localState);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("profile", out var profile) &&
                profile.TryGetProperty("last_used", out var lastUsed) &&
                lastUsed.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var dir = lastUsed.GetString();
                if (!string.IsNullOrEmpty(dir))
                    return $"--profile-directory=\"{dir}\"";
            }
        }
        catch { }
        return null;
    }

    private static void SetAutoStartRegistry(bool enable)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;

            if (enable)
            {
                var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                    key.SetValue("WinAssistant", exePath);
            }
            else
            {
                if (key.GetValue("WinAssistant") != null)
                    key.DeleteValue("WinAssistant");
            }
        }
        catch { }
    }
}

public class HotKeyBindingViewModel : ObservableObject
{
    private Microsoft.UI.Xaml.Media.ImageSource? _iconSource;

    public HotKeyBinding Model { get; }

    public HotKeyBindingViewModel(HotKeyBinding model)
    {
        Model = model;
    }

    public string Name => Model.Name;
    public string AppPath => Model.AppPath;

    public Microsoft.UI.Xaml.Media.ImageSource? IconSource
    {
        get => _iconSource;
        set => SetProperty(ref _iconSource, value);
    }

    public string HotKeyDisplayText =>
        !string.IsNullOrEmpty(Model.HotKeyDisplay)
            ? Model.HotKeyDisplay
            : "未设置";

    public bool IsEnabled
    {
        get => Model.IsEnabled;
        set
        {
            if (Model.IsEnabled != value)
            {
                Model.IsEnabled = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HotKeyDisplayText));
            }
        }
    }

    public void NotifyHotKeyChanged()
    {
        OnPropertyChanged(nameof(HotKeyDisplayText));
    }
}

public class AppPickerViewModel : ObservableObject
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
            : _allApps.Where(a => a.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase)
                               || a.AppPath.Contains(searchText, StringComparison.OrdinalIgnoreCase));

        foreach (var item in source)
            _filteredApps.Add(item);
    }
}
