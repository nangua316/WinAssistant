using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
            // Auto-select first item when filter results change.
            if (e.PropertyName == nameof(LaunchpadPageViewModel.FilteredItems))
                SelectFirstItem();
        };
        SizeChanged += OnPageSizeChanged;
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateItemSize();
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
        // Delay focus so the visual tree is ready
        _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            SearchBox.Focus(FocusState.Programmatic));
    }

    private void UpdateItemSize()
    {
        if (AppGrid.ItemsPanelRoot is ItemsWrapGrid wrapGrid)
        {
            var availableWidth = ActualWidth - 48; // 24px padding each side
            var maxColumns = 10;
            var itemWidth = Math.Max(120, (int)(availableWidth / maxColumns));
            wrapGrid.ItemWidth = itemWidth;
        }
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
                // First Escape clears search, second Escape closes
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
}
