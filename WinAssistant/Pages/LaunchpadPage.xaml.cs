using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
        if (string.IsNullOrWhiteSpace(ViewModel.SearchText))
        {
            AppGrid.SelectedItem = null;
            return;
        }
        if (ViewModel.FilteredItems.Count > 0 && AppGrid.SelectedItem == null)
            App.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => AppGrid.SelectedIndex = 0);
    }

    public void Activate()
    {
        ViewModel.LoadItems();
        ViewModel.SetXamlRoot(this.XamlRoot);
        SearchBox.Text = "";
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
        var addItem = new MenuFlyoutItem { Text = "+ 添加应用" };
        addItem.Click += (s, args) => ViewModel.AddAppCommand.Execute(null);
        menu.Items.Add(addItem);
        menu.ShowAt((UIElement)sender, e.GetPosition((UIElement)sender));
        e.Handled = true;
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
            var action = AppLauncher.LaunchOrActivate(vm.AppPath, vm.Model.Arguments, vm.Model.Aumid);
            ShowLaunchToast(action, vm.Name);
            Close();
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
            else
            {
                var action = AppLauncher.LaunchOrActivate(vm.AppPath, vm.Model.Arguments, vm.Model.Aumid);
                ShowLaunchToast(action, vm.Name);
                Close();
            }
        }
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

    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

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
