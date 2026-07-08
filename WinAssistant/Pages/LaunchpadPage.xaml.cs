using Microsoft.Windows.Storage.Pickers;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;
using WinAssistant.Controls.AiChat;
using WinAssistant.Controls.Tools;
using WinAssistant.Helpers;
using WinAssistant.Models;
using WinAssistant.ViewModels;

namespace WinAssistant.Pages;

public sealed partial class LaunchpadPage : Page
{
    private LaunchpadDragHandler? _dragHandler;
    private CancellationTokenSource? _fetchCts;

    // ── Manual window drag state ──
    private bool _isDragging;
    private POINT _dragStartCursor;
    private RECT _dragStartWindowRect;

    // ── Manual window resize state ──
    private bool _isResizing;
    private POINT _resizeStartCursor;
    private RECT _resizeStartWindowRect;

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

        // 移除文本框内置删除按钮（在自定义 TextBox 的 OnApplyTemplate 中处理）
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
            (a, b) => ViewModel.SwapItems(a, b),
            item => ViewModel.MoveItemToEnd(item),
            GetThemedBrush("ItemNameBrush"),
            GetThemedBrush("AccentBrush"),
            GetThemedBrush("DragGhostBackgroundBrush"),
            GetThemedBrush("TextPrimaryBrush"),
            GetThemedBrush("ItemFallbackBrush"));
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
            GetThemedBrush("ItemNameBrush"),
            GetThemedBrush("AccentBrush"),
            GetThemedBrush("DragGhostBackgroundBrush"),
            GetThemedBrush("TextPrimaryBrush"),
            GetThemedBrush("ItemFallbackBrush"));
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

        // 同步刷新 drag handler 的 brush，避免主题切换后 ghost 用旧颜色
        _dragHandler?.UpdateBrushes(
            GetThemedBrush("ItemNameBrush"),
            GetThemedBrush("AccentBrush"),
            GetThemedBrush("DragGhostBackgroundBrush"),
            GetThemedBrush("TextPrimaryBrush"),
            GetThemedBrush("ItemFallbackBrush"));

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

    private static DependencyObject? FindChildByName(DependencyObject parent, string name)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement fe && fe.Name == name)
                return child;
            var found = FindChildByName(child, name);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>
    /// 显式从当前主题字典读取 brush，避免代码中直接 Resources[key]
    /// 在主题切换后仍解析为旧值的问题。
    /// </summary>
    private Brush GetThemedBrush(string key)
    {
        var themeKey = App.CurrentTheme == ApplicationTheme.Light ? "Light" : "Dark";
        if (Resources.ThemeDictionaries.TryGetValue(themeKey, out var dictObj)
            && dictObj is ResourceDictionary themeDict
            && themeDict.TryGetValue(key, out var brush))
        {
            return (Brush)brush;
        }
        return (Brush)Resources[key];
    }

    // ── Manual window drag ──

    private void OnDragHeaderPressed(object sender, PointerRoutedEventArgs e)
    {
        if (OwnerHwnd is nint hwnd && hwnd != nint.Zero && GetCursorPos(out _dragStartCursor))
        {
            _isDragging = true;
            GetWindowRect(hwnd, out _dragStartWindowRect);
            SetCapture(hwnd);
            ((UIElement)sender).CapturePointer(e.Pointer);
            e.Handled = true;
        }
    }

    private void OnDragHeaderMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging || OwnerHwnd is not nint hwnd) return;
        if (!GetCursorPos(out var cur)) return;

        var dx = cur.X - _dragStartCursor.X;
        var dy = cur.Y - _dragStartCursor.Y;

        SetWindowPos(hwnd, nint.Zero,
            _dragStartWindowRect.left + dx,
            _dragStartWindowRect.top + dy,
            0, 0, SWP_NOSIZE | SWP_NOZORDER);
    }

    private void OnDragHeaderReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            if (OwnerHwnd is nint hwnd && hwnd != nint.Zero)
                ReleaseCapture();
            ((UIElement)sender).ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
    }

    private void OnDragHeaderCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            if (OwnerHwnd is nint hwnd && hwnd != nint.Zero)
                ReleaseCapture();
        }
    }

    // ── Manual window resize ──

    private void OnResizeGripPressed(object sender, PointerRoutedEventArgs e)
    {
        if (OwnerHwnd is nint hwnd && hwnd != nint.Zero && GetCursorPos(out _resizeStartCursor))
        {
            _isResizing = true;
            GetWindowRect(hwnd, out _resizeStartWindowRect);
            SetCapture(hwnd);
            ((UIElement)sender).CapturePointer(e.Pointer);
            e.Handled = true;
        }
    }

    private void OnResizeGripMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isResizing || OwnerHwnd is not nint hwnd) return;
        if (!GetCursorPos(out var cur)) return;

        var dx = cur.X - _resizeStartCursor.X;
        var dy = cur.Y - _resizeStartCursor.Y;

        var startW = _resizeStartWindowRect.right - _resizeStartWindowRect.left;
        var startH = _resizeStartWindowRect.bottom - _resizeStartWindowRect.top;
        var newW = Math.Max(400, startW + dx);
        var newH = Math.Max(300, startH + dy);

        SetWindowPos(hwnd, nint.Zero, 0, 0, newW, newH, SWP_NOMOVE | SWP_NOZORDER);
    }

    private void OnResizeGripReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isResizing)
        {
            _isResizing = false;
            if (OwnerHwnd is nint hwnd && hwnd != nint.Zero)
            {
                ReleaseCapture();
                if (GetWindowRect(hwnd, out var rect))
                {
                    var settings = App.SettingsService.Load();
                    settings.LaunchpadWindowWidth = rect.right - rect.left;
                    settings.LaunchpadWindowHeight = rect.bottom - rect.top;
                    App.SettingsService.Save(settings);
                }
            }
            ((UIElement)sender).ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
    }

    private void OnResizeGripCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_isResizing)
        {
            _isResizing = false;
            if (OwnerHwnd is nint hwnd && hwnd != nint.Zero)
                ReleaseCapture();
        }
    }

    private void OnPinToggle(object sender, RoutedEventArgs e)
    {
        _isPinned = PinButton.IsChecked == true;
        PinChanged?.Invoke(this, _isPinned);
    }

    /// <summary>
    /// Builds the page-level context menu (添加应用, 添加文件夹, … 打开设置).
    /// Shared between right-click and the bottom-right hamburger button.
    /// </summary>
    private MenuFlyout CreatePageMenu()
    {
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
        var fileItem = new MenuFlyoutItem
        {
            Text = "添加文件",
            Icon = new FontIcon { Glyph = "" }
        };
        fileItem.Click += OnAddFileClick;
        menu.Items.Add(fileItem);
        var urlItem = new MenuFlyoutItem
        {
            Text = "添加网址",
            Icon = new FontIcon { Glyph = "" }
        };
        urlItem.Click += OnAddUrlClick;
        menu.Items.Add(urlItem);
        var scriptItem = new MenuFlyoutItem
        {
            Text = "添加脚本",
            Icon = new FontIcon { Glyph = "" }
        };
        scriptItem.Click += OnAddScriptClick;
        menu.Items.Add(scriptItem);
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
        return menu;
    }

    private void OnMenuButtonClick(object sender, RoutedEventArgs e)
    {
        var menu = CreatePageMenu();
        if (sender is FrameworkElement fe)
            menu.ShowAt(fe);
    }

    private void OnPageRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var point = e.GetPosition(AppGrid);
        var hitItem = _dragHandler?.FindItemAt(point);
        if (hitItem != null) return;

        var menu = CreatePageMenu();
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

    private async void OnAddFileClick(object? sender, RoutedEventArgs e)
    {
        PinChanged?.Invoke(this, true);
        SearchBox.IsEnabled = false;
        try
        {
            var hwnd = OwnerHwnd ?? WinRT.Interop.WindowNative.GetWindowHandle(App.Window);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var picker = new FileOpenPicker(windowId)
            {
                SuggestedStartLocation = PickerLocationId.Desktop,
                ViewMode = PickerViewMode.List
            };
            // No type filter — show all files
            var file = await picker.PickSingleFileAsync();
            if (file != null)
                ViewModel.AddFileItem(file.Path, System.IO.Path.GetFileNameWithoutExtension(file.Path));
        }
        finally
        {
            SearchBox.IsEnabled = true;
            PinChanged?.Invoke(this, IsPinned);
        }
    }

    private async void OnAddUrlClick(object? sender, RoutedEventArgs e)
    {
        await ShowUrlDialog(null, null, null, null);
    }

    private async void OnAddScriptClick(object? sender, RoutedEventArgs e)
    {
        Logger.Log("ShowScriptDialog", "OnAddScriptClick called");
        await ShowScriptDialog(null, null);
        Logger.Log("ShowScriptDialog", "OnAddScriptClick completed");
    }

    /// <summary>Preset Segoe MDL2 Assets glyphs for script icon picker.</summary>
    private static readonly string[] PresetScriptIcons =
    [
        "", // Tools
        "", // Settings
        "", // Lightning bolt
        "", // Refresh
        "", // Link
        "", // Folder
        "", // Launch
        "", // Package
        "", // Lock
        "", // Unlock
        "", // Desktop
        "", // Search
        "", // Delete
        "", // Edit
        "", // Save
        "", // Power
        "", // Repeat
        "", // Globe
        "", // Download
        "", // Upload
        "", // Code
        "", // Clipboard
    ];

    /// <summary>Show the add/edit script dialog.</summary>
    private async Task ShowScriptDialog(string? existingName, string? existingScript)
    {
        Logger.Log("ShowScriptDialog", $"ShowScriptDialog called, isEdit={existingName != null}");
        Logger.Log("ScriptDebug", $"ShowScriptDialog: existingName='{existingName}', existingScript.Length={existingScript?.Length ?? -1}");
        if (existingScript != null)
            Logger.Log("ScriptDebug", $"  first 50 chars: [{existingScript[..Math.Min(50, existingScript.Length)]}]");
        var isEdit = existingName != null;

        var nameBox = new TextBox
        {
            PlaceholderText = "例如：重启网络",
            Header = "脚本名称",
            Text = existingName ?? ""
        };

        var scriptBox = new TextBox
        {
            PlaceholderText = "PowerShell 命令，例如：ipconfig /flushdns",
            Header = "脚本内容",
            // AcceptsReturn先于Text设置，否则多行内容初始化时会被截断
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 100,
            MaxHeight = 500
        };
        scriptBox.Text = NormalizeScriptForDisplay(existingScript);

        // ── Icon picker (monochrome, centered rows) ──
        string? selectedGlyph = null;
        var allBorders = new List<Border>();

        var iconHeader = new TextBlock
        {
            Text = "图标",
            FontSize = 12,
            Opacity = 0.7
        };

        var iconGrid = new StackPanel { Spacing = 6 };

        var allIcons = new List<string?> { null }; // null = 无图标
        allIcons.AddRange(PresetScriptIcons);
        const int cols = 8;

        for (int r = 0; r < (allIcons.Count + cols - 1) / cols; r++)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8
            };
            int start = r * cols;
            int end = Math.Min(start + cols, allIcons.Count);
            for (int i = start; i < end; i++)
            {
                var btn = MakeIconButton(allIcons[i]);
                row.Children.Add(btn);
                allBorders.Add(btn);
            }
            iconGrid.Children.Add(row);
        }

        // Pre-select existing glyph when editing
        if (isEdit)
        {
            var existingItem = ViewModel.Items.FirstOrDefault(vm =>
                vm.Name == existingName && vm.Model.Script == existingScript);
            if (existingItem?.Model.FontIconGlyph is string g && !string.IsNullOrEmpty(g))
                SelectIcon(g);
        }

        var iconSection = new StackPanel { Spacing = 4, Margin = new Thickness(0, 6, 0, 0) };
        iconSection.Children.Add(iconHeader);
        iconSection.Children.Add(iconGrid);

        Border MakeIconButton(string? glyph)
        {
            var isDark = App.CurrentTheme == ApplicationTheme.Dark;
            var bgTint = isDark
                ? Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x12, 0x00, 0x00, 0x00);
            var fgColor = isDark
                ? Color.FromArgb(0xCC, 0xDD, 0xDD, 0xDD)
                : Color.FromArgb(0xCC, 0x3A, 0x3A, 0x3A);
            var noIconBg = isDark
                ? Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x08, 0x00, 0x00, 0x00);
            var noIconFg = isDark
                ? Color.FromArgb(0x88, 0xBB, 0xBB, 0xBB)
                : Color.FromArgb(0x88, 0x3A, 0x3A, 0x3A);

            var inner = new Border
            {
                Width = 36, Height = 36,
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(bgTint),
            };

            if (glyph != null)
            {
                inner.Child = new TextBlock
                {
                    Text = glyph,
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 20,
                    Foreground = new SolidColorBrush(fgColor),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
            }
            else
            {
                inner.Background = new SolidColorBrush(noIconBg);
                inner.Child = new TextBlock
                {
                    Text = "✕",
                    FontSize = 14,
                    Foreground = new SolidColorBrush(noIconFg),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                ToolTipService.SetToolTip(inner, "无图标（使用默认终端图标）");
            }

            var border = new Border
            {
                Width = 46, Height = 46,
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(2),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
                Padding = new Thickness(3),
                Tag = glyph,
                Child = inner
            };

            var captured = glyph;
            border.Tapped += (_, _) => SelectIcon(captured);
            return border;
        }

        void SelectIcon(string? glyph)
        {
            selectedGlyph = glyph;
            var accent = Color.FromArgb(0xFF, 0x1E, 0x90, 0xFF);
            var transparent = Color.FromArgb(0, 0, 0, 0);
            foreach (var b in allBorders)
            {
                var isSel = (string?)b.Tag == glyph;
                b.BorderBrush = new SolidColorBrush(isSel ? accent : transparent);
            }
        }

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(nameBox);
        panel.Children.Add(scriptBox);
        panel.Children.Add(iconSection);

        var scrollView = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        var dialog = new ContentDialog
        {
            Title = isEdit ? "编辑脚本" : "添加脚本",
            Content = scrollView,
            PrimaryButtonText = isEdit ? "保存" : "添加",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            MinWidth = 450,
            XamlRoot = this.XamlRoot
        };

        // Pre-size the script box when editing existing content
        if (!string.IsNullOrEmpty(existingScript))
        {
            var lineCount = existingScript.Count(c => c == '\n') + 1;
            scriptBox.MinHeight = Math.Min(20 * lineCount + 20, scriptBox.MaxHeight);
        }

        string? capturedName = null;
        string? capturedScript = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            capturedName = nameBox.Text.Trim();
            capturedScript = scriptBox.Text.Trim();
            Logger.Log("ShowScriptDialog", $"PrimaryButtonClick: name='{capturedName}', script.Length={capturedScript?.Length ?? -1}");

            if (string.IsNullOrEmpty(capturedName) || string.IsNullOrEmpty(capturedScript))
            {
                args.Cancel = true; // prevent closing
                Logger.Log("ShowScriptDialog", "Validation failed, dialog stays open");
            }
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        // Values captured in PrimaryButtonClick handler
        var finalName = capturedName!;
        var finalScript = capturedScript!.Replace("\r\n", "\r"); // ponytail: normalize to \r-only
        Logger.Log("ShowScriptDialog", $"After ShowAsync: finalName='{finalName}', finalScript.Length={finalScript?.Length ?? -1}");

        try
        {
            if (isEdit)
            {
                // Find and update the existing script item
                var item = ViewModel.Items.FirstOrDefault(vm =>
                    vm.Name == existingName && vm.Model.Script == existingScript);
                if (item != null)
                {
                    item.Model.Name = finalName;
                    item.Model.Script = finalScript;
                    item.Model.FontIconGlyph = selectedGlyph;
                    // Clear IconPath when user chose a glyph, fall back to terminal icon otherwise
                    if (!string.IsNullOrEmpty(selectedGlyph))
                        item.Model.IconPath = null;
                    else if (string.IsNullOrEmpty(item.Model.IconPath))
                        item.Model.IconPath = ExtractTerminalIcon();
                    ViewModel.SaveItems();
                    item.RefreshIconDisplay();
                    Logger.Log("ShowScriptDialog", "Edit saved OK");
                }
                else
                {
                    Logger.Log("ShowScriptDialog", "Edit: item not found in lookup");
                }
            }
            else
            {
                var iconPath = !string.IsNullOrEmpty(selectedGlyph) ? null : ExtractTerminalIcon();
                var launchpadItem = new LaunchpadItem
                {
                    Name = finalName,
                    Script = finalScript,
                    IconPath = iconPath,
                    FontIconGlyph = selectedGlyph
                };
                var vm = new LaunchpadItemViewModel(launchpadItem);
                ViewModel.Items.Add(vm);
                ViewModel.SaveItems();
                ViewModel.LoadItemIcon(vm);
                Logger.Log("ShowScriptDialog", "New item saved OK");
            }
        }
        catch (Exception ex)
        {
            Logger.Log("ShowScriptDialog", $"Save error: {ex}");
            await new ContentDialog
            {
                Title = "保存失败",
                Content = $"保存时出错:\n{ex.Message}",
                CloseButtonText = "确定",
                XamlRoot = this.XamlRoot
            }.ShowAsync();
        }
    }

    /// <summary>Normalize line endings for TextBox display: \r\n→\r, trim trailing whitespace per line.</summary>
    private static string NormalizeScriptForDisplay(string? script) =>
        script?.Replace("\r\n", "\r") ?? "";

    private static string? ExtractTerminalIcon()
    {
        var iconPath = GetTerminalIconPath();
        if (File.Exists(iconPath)) return iconPath; // already cached

        // Extract from the real WindowsTerminal.exe or powershell.exe
        try
        {
            var exePath = FindRealTerminalExe();
            if (!string.IsNullOrEmpty(exePath))
            {
                var extracted = IconHelper.ExtractAppIconToAppData(exePath, 64);
                if (extracted != null)
                {
                    // Copy to fixed location
                    Directory.CreateDirectory(Path.GetDirectoryName(iconPath)!);
                    File.Copy(extracted, iconPath, overwrite: true);
                    Logger.Log("LaunchpadPage", $"Terminal icon saved to {iconPath}");
                    return iconPath;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log("LaunchpadPage", $"ExtractTerminalIcon error: {ex.Message}");
        }
        return null;
    }

    /// <summary>Fixed path for the terminal icon, shared by all script items.</summary>
    private static string GetTerminalIconPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WinAssistant", "terminal-icon.png");

    /// <summary>Find the real WindowsTerminal.exe or fall back to powershell.exe.</summary>
    private static string FindRealTerminalExe()
    {
        try
        {
            var appsDir = @"C:\Program Files\WindowsApps";
            if (Directory.Exists(appsDir))
            {
                var dirs = Directory.GetDirectories(appsDir, "Microsoft.WindowsTerminal_*");
                if (dirs.Length > 0)
                {
                    Array.Sort(dirs);
                    var exe = Path.Combine(dirs[^1], "WindowsTerminal.exe");
                    if (File.Exists(exe)) return exe;
                }
            }
        }
        catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"WindowsPowerShell\v1.0\powershell.exe");
    }

    /// <summary>Show the add/edit URL dialog. Pass existing values to edit an existing item.</summary>
    private async Task ShowUrlDialog(string? existingName, string? existingUrl,
        string? existingBrowserPath, LaunchpadItem? existingItem)
    {
        var isEdit = existingItem != null;

        var urlBox = new TextBox
        {
            PlaceholderText = "例如：https://github.com",
            Header = "网址",
            Text = existingUrl ?? ""
        };

        // ── Icon selection area ──
        var iconSelector = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var iconSelectorLabel = new TextBlock
        {
            Text = "选择图标：",
            FontSize = 12,
            Opacity = 0.7,
            Visibility = Visibility.Collapsed
        };
        var iconSelectorContainer = new StackPanel { Spacing = 4, Visibility = Visibility.Collapsed };
        iconSelectorContainer.Children.Add(iconSelectorLabel);
        iconSelectorContainer.Children.Add(iconSelector);

        string? selectedIconPath = existingItem?.IconPath;
        // If the old icon file was deleted (e.g. temp cleaned), start fresh
        if (!string.IsNullOrEmpty(selectedIconPath) && !File.Exists(selectedIconPath))
            selectedIconPath = null;

        // ── Name & preview row ──
        var iconPreview = new Image
        {
            Width = 40, Height = 40,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var nameBox = new TextBox
        {
            PlaceholderText = "例如：GitHub",
            Header = "显示名称",
            Text = existingName ?? "",
            VerticalAlignment = VerticalAlignment.Center
        };
        bool _nameManuallyEdited = false;
        nameBox.TextChanged += (_, _) => _nameManuallyEdited = true;
        var iconNamePanel = new Grid { ColumnSpacing = 12 };
        iconNamePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        iconNamePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(iconPreview, 0);
        Grid.SetColumn(nameBox, 1);
        iconNamePanel.Children.Add(iconPreview);
        iconNamePanel.Children.Add(nameBox);

        // ── Fetch button ──
        var fetchButton = new Button
        {
            Content = "获取图标和标题",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        // ── Browser picker ──
        var browserOptions = new ObservableCollection<BrowserScanner.BrowserInfo>();
        browserOptions.Add(new BrowserScanner.BrowserInfo("使用系统默认浏览器", "", BrowserScanner.GetDefaultBrowserIcon()));
        foreach (var b in BrowserScanner.ScanInstalledBrowsers())
            browserOptions.Add(b);

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
                "</StackPanel></DataTemplate>")
        };

        // Pre-select existing browser when editing
        if (isEdit && !string.IsNullOrEmpty(existingBrowserPath))
        {
            var match = browserOptions.FirstOrDefault(b =>
                b.Path.Equals(existingBrowserPath, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                browserCombo.SelectedItem = match;
            else if (File.Exists(existingBrowserPath))
            {
                var custom = new BrowserScanner.BrowserInfo(
                    Path.GetFileNameWithoutExtension(existingBrowserPath),
                    existingBrowserPath, BrowserScanner.LoadBrowserIcon(existingBrowserPath));
                browserOptions.Insert(1, custom);
                browserCombo.SelectedItem = custom;
            }
        }

        void UpdatePreview()
        {
            iconPreview.Source = !string.IsNullOrEmpty(selectedIconPath) && File.Exists(selectedIconPath)
                ? new BitmapImage { UriSource = new Uri(selectedIconPath) }
                : (browserCombo.SelectedItem as BrowserScanner.BrowserInfo)?.IconSource;
        }

        browserCombo.SelectionChanged += (_, _) => UpdatePreview();

        // ── Auto-fetch on URL input ──
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

            // Cancel any previous fetch (avoids concurrent WebView2/HTTP requests)
            _fetchCts?.Cancel();
            _fetchCts?.Dispose();
            _fetchCts = new CancellationTokenSource();
            var ct = _fetchCts.Token;

            fetchButton.IsEnabled = false;
            fetchButton.Content = "获取中...";

            // Clear previous results
            iconSelector.Children.Clear();
            iconSelectorContainer.Visibility = Visibility.Collapsed;
            selectedIconPath = null;

            string? title = null;
            List<WebsiteMetadataHelper.IconOption> icons = [];

            // ── Phase 1: HTTP (fast, works for 95% of sites) ──
            if (!ct.IsCancellationRequested)
            {
                var httpTitleTask = WebsiteMetadataHelper.FetchTitleAsync(normalized);
                var httpIconsTask = WebsiteMetadataHelper.FetchAllIconsAsync(normalized);
                await Task.WhenAll(httpTitleTask, httpIconsTask);

                if (!ct.IsCancellationRequested)
                {
                    title = httpTitleTask.Result;
                    icons = httpIconsTask.Result;
                    ShowIconResults(title, icons);
                }
            }

            // ── After HTTP: if we have results, we're done ──
            if (!ct.IsCancellationRequested)
            {
                if (icons.Count > 0)
                {
                    fetchButton.IsEnabled = true;
                    fetchButton.Content = "重新获取";
                    return; // done, no WebView2 needed
                }

                // HTTP found nothing — keep "获取中..." (disabled), fire WebView2
                fetchButton.Content = "获取中...";
            }

            // ── Phase 2: WebView2 fallback ──
            if (XamlRoot != null && !ct.IsCancellationRequested && icons.Count == 0)
            {
                var capturedTitle = title;
                var capturedIcons = icons;
                _ = App.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    async () =>
                {
                    try
                    {
                        var wv2Info = await WebView2FaviconHelper.FetchAsync(normalized, XamlRoot);
                        if (!ct.IsCancellationRequested)
                        {
                            if (wv2Info?.FaviconSource != null)
                            {
                                var wv2Option = new WebsiteMetadataHelper.IconOption(
                                    "最佳", wv2Info.FaviconPath, wv2Info.FaviconSource, -1);
                                capturedIcons.Insert(0, wv2Option);
                            }
                            if (!string.IsNullOrEmpty(wv2Info?.Title))
                                capturedTitle = wv2Info.Title;
                            ShowIconResults(capturedTitle, capturedIcons);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log("LaunchpadPage", $"WebView2 fallback error: {ex.Message}");
                    }
                    finally
                    {
                        // WebView2 done — show result or failure
                        if (!ct.IsCancellationRequested)
                        {
                            fetchButton.IsEnabled = true;
                            if (capturedIcons.Count > 0 || !string.IsNullOrEmpty(capturedTitle))
                                fetchButton.Content = "重新获取";
                            else
                                fetchButton.Content = "获取失败";
                        }
                    }
                });
            }
        }
        void ShowIconResults(string? title, List<WebsiteMetadataHelper.IconOption> icons)
        {
            if (!string.IsNullOrEmpty(title) && !_nameManuallyEdited)
                nameBox.Text = title;

            iconSelector.Children.Clear();

            if (icons.Count > 0)
            {
                WebsiteMetadataHelper.IconOption? selectedOption = null;
                foreach (var icon in icons)
                {
                    var border = new Border
                    {
                        Width = 44, Height = 44,
                        BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x80, 0x80, 0x80)),
                        BorderThickness = new Thickness(2),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(2),
                        Background = new SolidColorBrush(Color.FromArgb(0x00, 0x00, 0x00, 0x00)),
                        Tag = icon
                    };

                    var img = new Image
                    {
                        Source = icon.Source,
                        Width = 32, Height = 32,
                        Stretch = Stretch.Uniform
                    };
                    border.Child = img;

                    // Click to select
                    border.Tapped += (_, _) =>
                    {
                        foreach (var child in iconSelector.Children)
                        {
                            if (child is Border b)
                            {
                                b.BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x80, 0x80, 0x80));
                                b.BorderThickness = new Thickness(2);
                            }
                        }
                        border.BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x1E, 0x90, 0xFF));
                        border.BorderThickness = new Thickness(3);
                        selectedIconPath = icon.Path;
                        selectedOption = icon;
                        UpdatePreview();
                    };

                    ToolTipService.SetToolTip(border, icon.Label);
                    iconSelector.Children.Add(border);
                }

                // Default: select first
                if (iconSelector.Children.FirstOrDefault() is Border firstBorder)
                {
                    firstBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x1E, 0x90, 0xFF));
                    firstBorder.BorderThickness = new Thickness(3);
                    selectedIconPath = icons[0].Path;
                    selectedOption = icons[0];
                }

                iconSelectorLabel.Visibility = Visibility.Visible;
                iconSelectorContainer.Visibility = Visibility.Visible;
            }

            UpdatePreview();
        }

        // ── Dialog layout ──
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(urlBox);
        panel.Children.Add(fetchButton);
        panel.Children.Add(iconNamePanel);
        panel.Children.Add(iconSelectorContainer);
        panel.Children.Add(browserCombo);

        var dialog = new ContentDialog
        {
            Title = isEdit ? "编辑网址" : "添加网址",
            Content = panel,
            PrimaryButtonText = isEdit ? "保存" : "添加",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };

        UpdatePreview();

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        var finalName = nameBox.Text.Trim();
        var finalUrl = urlBox.Text.Trim();
        var browserPath = (browserCombo.SelectedItem as BrowserScanner.BrowserInfo)?.Path ?? "";

        if (string.IsNullOrEmpty(finalName) || string.IsNullOrEmpty(finalUrl))
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

        if (isEdit)
        {
            // Update existing item
            existingItem.Name = finalName;
            existingItem.Url = NormalizeUrl(finalUrl);
            existingItem.BrowserPath = browserPath;
            if (!string.IsNullOrEmpty(selectedIconPath))
                existingItem.IconPath = selectedIconPath;
            ViewModel.SaveItems();
        }
        else
        {
            ViewModel.AddUrlItem(finalName, NormalizeUrl(finalUrl), browserPath,
                !string.IsNullOrEmpty(selectedIconPath) ? selectedIconPath : null);
        }
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

        if (!string.IsNullOrEmpty(vm.Model.Script))
        {
            var script = vm.Model.Script;
            _ = Task.Run(() =>
            {
                try
                {
                    var tempScript = Path.Combine(Path.GetTempPath(), "WinAssistant", "scripts",
                        $"{Guid.NewGuid():N}.ps1");
                    Directory.CreateDirectory(Path.GetDirectoryName(tempScript)!);
                    File.WriteAllText(tempScript, script, new UTF8Encoding(true));

                    var psArgs = $"-ExecutionPolicy Bypass -NoProfile -File \"{tempScript}\"";
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "wt.exe",
                            Arguments = $"powershell {psArgs}",
                            UseShellExecute = true
                        });
                    }
                    catch
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "powershell.exe",
                            Arguments = psArgs,
                            UseShellExecute = true
                        });
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log("LaunchpadPage", $"Script execution error: {ex.Message}");
                }
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

    private async void OnEditUrlItem(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item ||
            item.DataContext is not LaunchpadItemViewModel vm)
            return;

        if (!string.IsNullOrEmpty(vm.Model.Script))
        {
            Logger.Log("ScriptDebug", $"OnEditUrlItem: Name='{vm.Model.Name}', Script.Length={vm.Model.Script.Length}");
            Logger.Log("ScriptDebug", $"Script starts: [{vm.Model.Script[..Math.Min(50, vm.Model.Script.Length)]}]");
            await ShowScriptDialog(vm.Model.Name, vm.Model.Script);
            return;
        }
        if (!string.IsNullOrEmpty(vm.Model.Url))
        {
            await ShowUrlDialog(vm.Model.Name, vm.Model.Url, vm.Model.BrowserPath, vm.Model);
            return;
        }
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

    // ── Win32 window position ──

    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern nint SetCapture(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left; public int top; public int right; public int bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
}
