using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinAssistant.Helpers;
using WinAssistant.ViewModels;

namespace WinAssistant.Pages;

public sealed partial class LaunchpadPage : Page
{
    public LaunchpadPageViewModel ViewModel { get; }

    /// <summary>Fired when the user wants to close the launchpad.</summary>
    public event EventHandler? CloseRequested;

    public LaunchpadPage()
    {
        InitializeComponent();
        ViewModel = new LaunchpadPageViewModel();
        ViewModel.Items.CollectionChanged += (s, e) => UpdateEmptyState();
        ViewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(LaunchpadPageViewModel.SearchText))
                AppGrid.CanReorderItems = string.IsNullOrEmpty(ViewModel.SearchText);
            if (e.PropertyName == nameof(LaunchpadPageViewModel.FilteredItems))
                SelectFirstItem();
        };
        SizeChanged += (_, _) => UpdateItemSize();
        AppGrid.Loaded += (_, _) => UpdateItemSize();
    }

    private void SelectFirstItem()
    {
        if (ViewModel.FilteredItems.Count > 0 && AppGrid.SelectedItem == null)
            App.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => AppGrid.SelectedIndex = 0);
    }

    public void Activate()
    {
        ViewModel.LoadItems();
        ViewModel.SetXamlRoot(this.XamlRoot);
        SearchBox.Text = "";
        UpdateItemSize();
        // Retry after layout settles
        _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            UpdateItemSize();
            SearchBox.Focus(FocusState.Programmatic);
        });
    }

    /// <summary>
    /// Finds the ItemsWrapGrid panel and sets ItemWidth for 6-column layout.
    /// Uses VisualTreeHelper to find the panel — ItemsPanelRoot may be null
    /// at activation time on high-DPI systems.
    /// </summary>
    private void UpdateItemSize()
    {
        var wrapGrid = FindWrapGrid();
        if (wrapGrid == null)
        {
            Log($"UpdateItemSize: ItemsWrapGrid not found, ActualWidth={ActualWidth}");
            return;
        }

        var availableWidth = Math.Max(200, ActualWidth - 40);
        var maxColumns = 8;
        var itemWidth = Math.Max(72, (int)(availableWidth / maxColumns));
        wrapGrid.ItemWidth = itemWidth;
        AppGrid.InvalidateMeasure();
        AppGrid.InvalidateArrange();
        Log($"UpdateItemSize: ActualWidth={ActualWidth}, availableWidth={availableWidth}, itemWidth={itemWidth}");
    }

    private ItemsWrapGrid? FindWrapGrid()
    {
        // ItemsPanelRoot is the fastest path
        if (AppGrid.ItemsPanelRoot is ItemsWrapGrid wg) return wg;
        // Fallback: walk the visual tree
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

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
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
            // Activate first while launchpad is foreground (has foreground privilege),
            // then close launchpad to reveal the activated window.
            AppLauncher.LaunchOrActivate(vm.AppPath, vm.Model.Arguments, vm.Model.Aumid);
            Close();
        }
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is LaunchpadItemViewModel vm)
        {
            AppLauncher.LaunchOrActivate(vm.AppPath, vm.Model.Arguments, vm.Model.Aumid);
            Close();
        }
    }

    private async void OnRemoveItem(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item &&
            item.DataContext is LaunchpadItemViewModel vm)
        {
            var dialog = new ContentDialog
            {
                Title = "移除应用",
                Content = $"确定要从 Launchpad 移除 \"{vm.Name}\"？",
                PrimaryButtonText = "移除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                ViewModel.RemoveItem(vm);
            }
        }
    }

    private void OnDragItemsCompleted(object sender, DragItemsCompletedEventArgs e)
    {
        if (e.DropResult == Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move)
        {
            ViewModel.SaveItems();
        }
    }

    private void UpdateEmptyState()
    {
        var hasItems = ViewModel.HasItems;
        var hasFilterResults = ViewModel.FilteredItems.Count > 0;

        if (!hasItems)
        {
            EmptyStatePanel.Visibility = Visibility.Visible;
            NoResultsPanel.Visibility = Visibility.Collapsed;
        }
        else if (!hasFilterResults)
        {
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            NoResultsPanel.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            NoResultsPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void Close()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private static void Log(string msg)
    {
        try { System.IO.File.AppendAllText(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WinAssistant_dbg.txt"),
            $"[{DateTime.Now:HH:mm:ss.fff}] LaunchpadPage: {msg}{Environment.NewLine}"); }
        catch { }
    }
}
