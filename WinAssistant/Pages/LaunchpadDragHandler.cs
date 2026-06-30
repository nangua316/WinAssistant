using System.Collections.ObjectModel;
using Windows.Foundation;
using WinAssistant.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using WinAssistant.ViewModels;

namespace WinAssistant.Pages;

/// <summary>
/// Handles custom pointer-based drag-to-swap reordering for the Launchpad GridView.
/// Dragging an icon over another icon highlights the target; on drop the two icons swap positions.
/// Extracted from LaunchpadPage.xaml.cs to keep page lifecycle separate from drag state.
/// </summary>
internal sealed class LaunchpadDragHandler : IDisposable
{
    public void Dispose()
    {
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
    private readonly Action<LaunchpadItemViewModel, LaunchpadItemViewModel> _swapItems;
    private readonly Action<LaunchpadItemViewModel> _moveItemToEnd;
    private Brush _itemNameBrush;
    private Brush _accentBrush;

    private LaunchpadItemViewModel? _pressedItem;
    private Point _pressedPoint;
    private bool _isDragging;
    private Border? _dragGhost;

    private LaunchpadItemViewModel? _currentTargetItem;
    private readonly List<GridViewItem> _disabledContainers = new();

    internal LaunchpadDragHandler(
        GridView gridView,
        Canvas dragCanvas,
        ObservableCollection<LaunchpadItemViewModel> items,
        Func<ObservableCollection<LaunchpadItemViewModel>> filteredItemsGetter,
        Func<bool> isSearchActive,
        Action<LaunchpadItemViewModel, LaunchpadItemViewModel> swapItems,
        Action<LaunchpadItemViewModel> moveItemToEnd,
        Brush itemNameBrush,
        Brush accentBrush)
    {
        _gridView = gridView;
        _dragCanvas = dragCanvas;
        _items = items;
        _filteredItemsGetter = filteredItemsGetter;
        _isSearchActive = isSearchActive;
        _swapItems = swapItems;
        _moveItemToEnd = moveItemToEnd;
        _itemNameBrush = itemNameBrush;
        _accentBrush = accentBrush;

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

            // Highlight the item currently under the pointer as the swap target.
            var target = FindItemAt(pt.Position);
            if (target == _pressedItem) target = null;
            UpdateDragTarget(target);

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
            var pt = e.GetCurrentPoint(_gridView).Position;
            var target = FindItemAt(pt);
            if (target != null && target != _pressedItem)
            {
                AnimateSwap(_pressedItem, target);
            }
            else if (CalcInsertIndex(pt) >= _items.Count)
            {
                // Dropped in trailing empty space: move to end with a controlled slide animation.
                AnimateMoveToEnd(_pressedItem);
            }

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

        UpdateDragTarget(null);

        if (_pressedItem != null)
            _pressedItem.IsBeingDragged = false;

        _currentTargetItem = null;
        _pressedItem = null;
        _pressedPoint = default;
        _isDragging = false;
    }

    /// <summary>Animates a swap between two items by translating their containers
    /// across each other, avoiding the intermediate shifts that come from
    /// ObservableCollection.Move-based swap animations.</summary>
    private void AnimateSwap(LaunchpadItemViewModel a, LaunchpadItemViewModel b)
    {
        var aIdx = _items.IndexOf(a);
        var bIdx = _items.IndexOf(b);
        if (aIdx < 0 || bIdx < 0 || aIdx == bIdx)
        {
            _swapItems(a, b);
            return;
        }

        // Hide ghost early so it doesn't overlap the animated icons.
        if (_dragGhost != null)
            _dragGhost.Visibility = Visibility.Collapsed;

        var containerA = _gridView.ContainerFromIndex(aIdx) as GridViewItem;
        var containerB = _gridView.ContainerFromIndex(bIdx) as GridViewItem;
        if (containerA == null || containerB == null)
        {
            _swapItems(a, b);
            return;
        }

        var posA = containerA.TransformToVisual(_gridView).TransformPoint(new Point(0, 0));
        var posB = containerB.TransformToVisual(_gridView).TransformPoint(new Point(0, 0));

        // Disable default transitions so the collection swap applies instantly.
        var originalTransitions = _gridView.ItemContainerTransitions;
        try
        {
            _gridView.ItemContainerTransitions = null;
            _swapItems(a, b);
            _gridView.UpdateLayout();
        }
        finally
        {
            _gridView.ItemContainerTransitions = originalTransitions;
        }

        var newContainerA = _gridView.ContainerFromIndex(aIdx) as GridViewItem;
        var newContainerB = _gridView.ContainerFromIndex(bIdx) as GridViewItem;
        if (newContainerA == null || newContainerB == null) return;

        var newPosA = newContainerA.TransformToVisual(_gridView).TransformPoint(new Point(0, 0));
        var newPosB = newContainerB.TransformToVisual(_gridView).TransformPoint(new Point(0, 0));

        var translateA = new TranslateTransform
        {
            X = posB.X - newPosA.X,
            Y = posB.Y - newPosA.Y
        };
        var translateB = new TranslateTransform
        {
            X = posA.X - newPosB.X,
            Y = posA.Y - newPosB.Y
        };

        newContainerA.RenderTransform = translateA;
        newContainerB.RenderTransform = translateB;

        const double durationMs = 200;
        var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };

        var storyboard = new Storyboard();
        void AddAnim(TranslateTransform target, string property)
        {
            var anim = new DoubleAnimation
            {
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
                EasingFunction = easing
            };
            Storyboard.SetTarget(anim, target);
            Storyboard.SetTargetProperty(anim, property);
            storyboard.Children.Add(anim);
        }

        AddAnim(translateA, "X");
        AddAnim(translateA, "Y");
        AddAnim(translateB, "X");
        AddAnim(translateB, "Y");

        storyboard.Completed += (_, _) =>
        {
            newContainerA.RenderTransform = null;
            newContainerB.RenderTransform = null;
        };

        storyboard.Begin();
    }

    /// <summary>Animates moving an item to the end of the grid. Every affected
    /// container is translated from its old visual position to its new one,
    /// replacing the default RepositionThemeTransition with a controlled
    /// 200ms ease-in-out slide.</summary>
    private void AnimateMoveToEnd(LaunchpadItemViewModel item)
    {
        var fromIdx = _items.IndexOf(item);
        if (fromIdx < 0 || fromIdx == _items.Count - 1) return;

        // Hide ghost early so it doesn't overlap the animated icons.
        if (_dragGhost != null)
            _dragGhost.Visibility = Visibility.Collapsed;

        var toIdx = _items.Count - 1;

        // Capture old visual positions of the affected range.
        var oldPositions = new Dictionary<int, Point>();
        for (int i = fromIdx; i <= toIdx; i++)
        {
            if (_gridView.ContainerFromIndex(i) is GridViewItem c)
                oldPositions[i] = c.TransformToVisual(_gridView).TransformPoint(new Point(0, 0));
        }

        if (oldPositions.Count == 0)
        {
            _moveItemToEnd(item);
            return;
        }

        // Disable default transitions so the collection change applies instantly.
        var originalTransitions = _gridView.ItemContainerTransitions;
        try
        {
            _gridView.ItemContainerTransitions = null;
            _moveItemToEnd(item);
            _gridView.UpdateLayout();
        }
        finally
        {
            _gridView.ItemContainerTransitions = originalTransitions;
        }

        const double durationMs = 200;
        var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var storyboard = new Storyboard();
        var animatedContainers = new List<GridViewItem>();

        for (int i = fromIdx; i <= toIdx; i++)
        {
            if (_gridView.ContainerFromIndex(i) is not GridViewItem newContainer) continue;

            // The item now at index i came from index i+1, except the last slot
            // which received the moved item from fromIdx.
            var oldItemIndex = (i == toIdx) ? fromIdx : i + 1;
            if (!oldPositions.TryGetValue(oldItemIndex, out var oldPos)) continue;

            var newPos = newContainer.TransformToVisual(_gridView).TransformPoint(new Point(0, 0));
            var translate = new TranslateTransform
            {
                X = oldPos.X - newPos.X,
                Y = oldPos.Y - newPos.Y
            };
            newContainer.RenderTransform = translate;
            animatedContainers.Add(newContainer);

            void AddAnim(string property)
            {
                var anim = new DoubleAnimation
                {
                    To = 0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
                    EasingFunction = easing
                };
                Storyboard.SetTarget(anim, translate);
                Storyboard.SetTargetProperty(anim, property);
                storyboard.Children.Add(anim);
            }
            AddAnim("X");
            AddAnim("Y");
        }

        if (storyboard.Children.Count == 0) return;

        storyboard.Completed += (_, _) =>
        {
            foreach (var c in animatedContainers)
                c.RenderTransform = null;
        };

        storyboard.Begin();
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

    private void UpdateDragTarget(LaunchpadItemViewModel? target)
    {
        if (ReferenceEquals(_currentTargetItem, target)) return;

        if (_currentTargetItem != null)
            _currentTargetItem.IsDragOverTarget = false;

        _currentTargetItem = target;

        if (_currentTargetItem != null)
            _currentTargetItem.IsDragOverTarget = true;
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
