using Microsoft.Windows.Storage.Pickers;
using System.Collections.Specialized;
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
        InitializeComponent();
        ViewModel = new LaunchpadPageViewModel();
        ViewModel.Items.CollectionChanged += OnItemsChanged;
        ViewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(LaunchpadPageViewModel.SearchText))
                UpdateReorderState();
            if (e.PropertyName == nameof(LaunchpadPageViewModel.FilteredItems))
                SelectFirstItem();
        };
        SizeChanged += (_, _) => UpdateItemSize();
        AppGrid.Loaded += (_, _) => UpdateItemSize();
        AppGrid.ItemContainerTransitions = null;
        _dragHandler = new LaunchpadDragHandler(
            AppGrid, DragCanvas,
            ViewModel.Items,
            () => ViewModel.FilteredItems,
            () => !string.IsNullOrWhiteSpace(ViewModel.SearchText),
            () => ViewModel.SaveItems(),
            (Brush)Resources["ItemNameBrush"],
            (Brush)Resources["AccentBrush"]);
        UpdateReorderState();
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

    private async void OnPageRightTapped(object sender, RightTappedRoutedEventArgs e)
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

    private void OnPageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
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
        if (AppGrid.SelectedItem == null && ViewModel.FilteredItems.Count > 0)
            AppGrid.SelectedIndex = 0;
        if (AppGrid.SelectedItem is LaunchpadItemViewModel vm)
        {
            if (HandleToolClick(vm)) return;
            var action = AppLauncher.LaunchOrActivate(vm.AppPath, vm.Model.Arguments, vm.Model.Aumid);
            ShowLaunchToast(action, vm.Name);
            Close(clearSearch: true);
        }
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is LaunchpadItemViewModel vm)
        {
            if (vm.IsUnadded)
            {
                ViewModel.AddUnaddedItem(vm);
            }
            else if (HandleToolClick(vm)) { }
            else
            {
                var action = AppLauncher.LaunchOrActivate(vm.AppPath, vm.Model.Arguments, vm.Model.Aumid);
                ShowLaunchToast(action, vm.Name);
                Close(clearSearch: true);
            }
        }
    }

    /// <summary>Handles tool item clicks. Returns true if the item is a tool (handled or window-opened).</summary>
    private bool HandleToolClick(LaunchpadItemViewModel vm)
    {
        if (!vm.IsTool || vm.Tool == null) return false;
        if (vm.Tool.IsOneClickAction)
        {
            var msg = vm.Tool.Activate();
            if (!string.IsNullOrEmpty(msg))
            {
                try { HotKeyToast.Show(msg); }
                catch { }
            }
            Close(clearSearch: true);
            return true;
        }
        ToolHostWindow.OpenOrActivate(vm.Tool);
        Close(clearSearch: true);
        return true;
    }

    private async void OnRemoveItem(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item &&
            item.DataContext is LaunchpadItemViewModel vm)
        {
            if (vm.IsUnadded)
            {
                // Just remove from the filter results; not a real item.
                ViewModel.FilteredItems.Remove(vm);
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
        AppGrid.IsItemClickEnabled = string.IsNullOrWhiteSpace(ViewModel.SearchText)
            || ViewModel.FilteredItems.Count > 0;
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

    private static void ShowLaunchToast(string action, string appName)
    {
        if (string.IsNullOrEmpty(action)) return;
        var verb = action switch
        {
            "minimize" => "最小化",
            "launch" => "打开",
            _ => "激活"
        };
        try { HotKeyToast.Show($"{verb} {appName}"); }
        catch { }
    }
}
