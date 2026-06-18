using System.Collections.ObjectModel;
using Windows.Foundation;
using WinAssistant.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinAssistant.ViewModels;

namespace WinAssistant.Pages;

/// <summary>
/// Handles custom pointer-based drag reordering for the Launchpad GridView.
/// Extracted from LaunchpadPage.xaml.cs to keep page lifecycle separate from drag state.
/// </summary>
internal sealed class LaunchpadDragHandler : IDisposable
{
    public void Dispose()
    {
        _dwellTimer.Stop();
        _gridView.RemoveHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPointerPressed));
        _gridView.RemoveHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnPointerMoved));
        _gridView.RemoveHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnPointerReleased));
        _gridView.RemoveHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnPointerCanceled));
    }

    private const double DragThreshold = 8;

    private readonly GridView _gridView;
    private readonly Canvas _dragCanvas;
    private readonly ObservableCollection<LaunchpadItemViewModel> _items;
    private readonly Func<ObservableCollection<LaunchpadItemViewModel>> _filteredItemsGetter;
    private readonly Func<bool> _isSearchActive;
    private readonly Action _saveItems;
    private Brush _itemNameBrush;
    private Brush _accentBrush;

    private LaunchpadItemViewModel? _pressedItem;
    private Point _pressedPoint;
    private bool _isDragging;
    private int _dragStartIndex = -1;
    private int _dragTargetIndex = -1;
    private Border? _dragGhost;

    // Placeholder item: replaces the pressed item in the collection during drag,
    // creating a visual gap that items animate around via RepositionThemeTransition.
    private LaunchpadItemViewModel? _placeholderItem;
    private int _originalDragIndex = -1;
    private int _pendingTargetIndex = -1;
    private readonly List<GridViewItem> _disabledContainers = new();
    private readonly DispatcherTimer _dwellTimer;

    internal LaunchpadDragHandler(
        GridView gridView,
        Canvas dragCanvas,
        ObservableCollection<LaunchpadItemViewModel> items,
        Func<ObservableCollection<LaunchpadItemViewModel>> filteredItemsGetter,
        Func<bool> isSearchActive,
        Action saveItems,
        Brush itemNameBrush,
        Brush accentBrush)
    {
        _gridView = gridView;
        _dragCanvas = dragCanvas;
        _items = items;
        _filteredItemsGetter = filteredItemsGetter;
        _isSearchActive = isSearchActive;
        _saveItems = saveItems;
        _itemNameBrush = itemNameBrush;
        _accentBrush = accentBrush;

        _dwellTimer = new DispatcherTimer();
        _dwellTimer.Interval = TimeSpan.FromMilliseconds(300);
        _dwellTimer.Tick += (_, _) =>
        {
            _dwellTimer.Stop();
            if (_pendingTargetIndex >= 0 && _placeholderItem != null)
            {
                var pIdx = _items.IndexOf(_placeholderItem);
                if (pIdx >= 0 && pIdx != _pendingTargetIndex)
                    _items.Move(pIdx, _pendingTargetIndex);
                _dragTargetIndex = _pendingTargetIndex;
            }
        };

        // handledEventsToo:true so we get events even if GridViewItem handles them
        gridView.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPointerPressed), true);
        gridView.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnPointerMoved), true);
        gridView.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnPointerReleased), true);
        gridView.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnPointerCanceled), true);

        // Catch newly created/recycled containers during drag and disable hit-test
        gridView.ContainerContentChanging += (_, args) =>
        {
            if (_isDragging && args.ItemContainer is GridViewItem c)
                c.IsHitTestVisible = false;
        };
    }

    /// <summary>主题切换时更新缓存的 Brush 引用。</summary>
    public void UpdateBrushes(Brush itemNameBrush, Brush accentBrush)
    {
        _itemNameBrush = itemNameBrush;
        _accentBrush = accentBrush;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_isSearchActive()) return;

        var pt = e.GetCurrentPoint(_gridView);
        if (!pt.Properties.IsLeftButtonPressed) return;

        var vm = FindItemAt(pt.Position);
        if (vm == null) return;

        _pressedItem = vm;
        _pressedPoint = pt.Position;
        _isDragging = false;
        _dragStartIndex = _items.IndexOf(vm);
        _dragTargetIndex = -1;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_pressedItem == null) return;

        var pt = e.GetCurrentPoint(_gridView);
        if (!pt.Properties.IsLeftButtonPressed)
        {
            CleanupDrag();
            return;
        }

        if (_isDragging)
        {
            Canvas.SetLeft(_dragGhost!, pt.Position.X - 45);
            Canvas.SetTop(_dragGhost!, pt.Position.Y - 55);

            // Calculate insertion index from pointer coordinates (grid math),
            // independent of collection state → no ping-pong shifts.
            var newTarget = CalcInsertIndex(pt.Position);
            // Clamp to 0..Count-1 for Move() safety.
            if (newTarget >= _items.Count) newTarget = _items.Count - 1;
            if (newTarget < 0) newTarget = 0;
            _pendingTargetIndex = newTarget;

            if (newTarget != _dragTargetIndex)
            {
                _dwellTimer.Stop();
                _dwellTimer.Start();
                _dragTargetIndex = newTarget;
            }

            e.Handled = true;
            return;
        }

        var dx = pt.Position.X - _pressedPoint.X;
        var dy = pt.Position.Y - _pressedPoint.Y;
        if (Math.Sqrt(dx * dx + dy * dy) >= DragThreshold)
        {
            StartDrag(e);
            e.Handled = true;
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_pressedItem == null) return;

        if (_isDragging)
        {
            _dwellTimer.Stop();

            // Calculate drop position from pointer coordinates (not placeholder position)
            var dropIdx = CalcInsertIndex(e.GetCurrentPoint(_gridView).Position);
            if (dropIdx > _items.Count) dropIdx = _items.Count;
            if (dropIdx < 0) dropIdx = 0;

            var pIdx = _items.IndexOf(_placeholderItem!);
            if (pIdx >= 0)
                _items.RemoveAt(pIdx);

            // Adjust drop index if placeholder was before it
            var insertIdx = dropIdx;
            if (pIdx >= 0 && pIdx < dropIdx) insertIdx--;

            _items.Insert(insertIdx, _pressedItem!);
            _saveItems();

            _placeholderItem = null; // prevent CleanupDrag from restoring original position
            CleanupDrag();
            e.Handled = true;
            return;
        }

        CleanupDrag();
    }

    private void OnPointerCanceled(object sender, PointerRoutedEventArgs e) => CleanupDrag();

    private void StartDrag(PointerRoutedEventArgs e)
    {
        if (_pressedItem == null) return;
        _isDragging = true;
        _pressedItem.IsBeingDragged = true;

        // Disable hover effects during drag
        _disabledContainers.Clear();
        for (int i = 0; i < _items.Count; i++)
        {
            if (_gridView.ContainerFromIndex(i) is GridViewItem c)
            {
                c.IsHitTestVisible = false;
                _disabledContainers.Add(c);
            }
        }

        // Replace pressed item with a placeholder so items "part" to show the gap.
        // The placeholder has Name="" and IconSource=null → renders as empty tile.
        _originalDragIndex = _dragStartIndex;
        _placeholderItem = new LaunchpadItemViewModel(new LaunchpadItem { Name = "" })
        {
            // Non-null IconSource keeps the fallback circle+letter hidden via NullToCollapsedConverter.
            IconSource = new BitmapImage()
        };
        // Remove the pressed item first so the grid has a real gap (count decreases by 1),
        // triggering RepositionThemeTransition to animate items into the empty slot.
        _items.RemoveAt(_originalDragIndex);
        _items.Insert(_originalDragIndex, _placeholderItem);
        _dragTargetIndex = _originalDragIndex;

        if (_dragGhost == null)
        {
            var icon = new Image
            {
                Width = 48,
                Height = 48,
                HorizontalAlignment = HorizontalAlignment.Center,
                Stretch = Stretch.Uniform
            };
            var label = new TextBlock
            {
                FontSize = 12,
                Foreground = _itemNameBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 80
            };
            var stack = new StackPanel { Spacing = 4, Margin = new Thickness(8) };
            stack.Children.Add(icon);
            stack.Children.Add(label);

            _dragGhost = new Border
            {
                Width = 90,
                Height = 100,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xDD, 0x1A, 0x1E, 0x30)),
                CornerRadius = new CornerRadius(10),
                BorderBrush = _accentBrush,
                BorderThickness = new Thickness(1),
                Child = stack,
                Visibility = Visibility.Collapsed
            };
            _dragCanvas.Children.Add(_dragGhost);
        }

        var stack2 = (StackPanel)_dragGhost.Child;
        ((Image)stack2.Children[0]).Source = _pressedItem.IconSource;
        ((TextBlock)stack2.Children[1]).Text = _pressedItem.Name;

        var pt = e.GetCurrentPoint(_gridView);
        Canvas.SetLeft(_dragGhost, pt.Position.X - 45);
        Canvas.SetTop(_dragGhost, pt.Position.Y - 55);
        _dragGhost.Visibility = Visibility.Visible;
    }

    private void CleanupDrag()
    {
        if (_dragGhost != null)
            _dragGhost.Visibility = Visibility.Collapsed;

        // Restore container hit-testing (re-enable hover effects)
        foreach (var c in _disabledContainers)
            c.IsHitTestVisible = true;
        _disabledContainers.Clear();

        // Cancel: if placeholder still exists (drag was canceled mid-flight),
        // restore the pressed item at its original position.
        if (_placeholderItem != null && _pressedItem != null)
        {
            var pIdx = _items.IndexOf(_placeholderItem);
            if (pIdx >= 0)
                _items.RemoveAt(pIdx);
            _items.Insert(_originalDragIndex, _pressedItem);
        }

        if (_pressedItem != null)
            _pressedItem.IsBeingDragged = false;

        _dwellTimer.Stop();
        _placeholderItem = null;
        _originalDragIndex = -1;
        _pendingTargetIndex = -1;
        _dragStartIndex = -1;
        _dragTargetIndex = -1;
        _pressedItem = null;
        _pressedPoint = default;
        _isDragging = false;
    }

    /// <summary>Finds the item at the given point using grid layout calculation.
    /// Uses the ItemsWrapGrid's slot dimensions (which include margins) and corrects
    /// for scroll offset so the row calculation matches the logical item index.</summary>
    internal LaunchpadItemViewModel? FindItemAt(Point point)
    {
        var items = _filteredItemsGetter();
        var wrapGrid = _gridView.ItemsPanelRoot as ItemsWrapGrid;
        if (wrapGrid == null || items.Count == 0) return null;

        var itemWidth = wrapGrid.ItemWidth;
        var itemHeight = wrapGrid.ItemHeight;
        var actualWidth = _gridView.ActualWidth;
        if (actualWidth <= 0 || itemWidth <= 0) return null;

        var scrollOffset = GetScrollOffset();
        var padding = _gridView.Padding;
        var maxColumns = Math.Max(1, (int)(actualWidth / itemWidth));
        var col = Math.Max(0, (int)((point.X - padding.Left) / itemWidth));
        var row = Math.Max(0, (int)((point.Y - padding.Top + scrollOffset) / itemHeight));
        if (col >= maxColumns) col = maxColumns - 1;

        var index = row * maxColumns + col;
        return index >= 0 && index < items.Count ? items[index] : null;
    }

    /// <summary>Calculates the logical insertion index from pointer coordinates,
    /// using the same grid math as FindItemAt but returning the raw index
    /// (independent of collection state) and allowing up to items.Count
    /// (insert at end).</summary>
    private int CalcInsertIndex(Point point)
    {
        var wrapGrid = _gridView.ItemsPanelRoot as ItemsWrapGrid;
        if (wrapGrid == null) return -1;

        var items = _filteredItemsGetter();
        var itemWidth = wrapGrid.ItemWidth;
        var itemHeight = wrapGrid.ItemHeight;
        var actualWidth = _gridView.ActualWidth;
        if (actualWidth <= 0 || itemWidth <= 0) return -1;

        var scrollOffset = GetScrollOffset();
        var padding = _gridView.Padding;
        var maxColumns = Math.Max(1, (int)(actualWidth / itemWidth));
        var col = Math.Max(0, (int)((point.X - padding.Left) / itemWidth));
        var row = Math.Max(0, (int)((point.Y - padding.Top + scrollOffset) / itemHeight));
        if (col >= maxColumns) col = maxColumns - 1;

        var index = row * maxColumns + col;
        // Only apply midpoint check when on the last item — prevents icon overlap
        // beyond the last cell while keeping normal cell-to-index mapping for all others.
        if (index >= items.Count) return items.Count;
        if (index == items.Count - 1)
        {
            var cellX = padding.Left + col * itemWidth;
            if (point.X > cellX + itemWidth / 2)
                return items.Count;
        }
        return index;
    }

    private double GetScrollOffset()
    {
        var sv = FindVisualChild<ScrollViewer>(_gridView);
        return sv?.VerticalOffset ?? 0;
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
}
