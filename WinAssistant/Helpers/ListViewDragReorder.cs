using System.Collections.ObjectModel;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.UI.Core;
using WinAssistant.ViewModels;

namespace WinAssistant.Helpers;

internal sealed class ListViewDragReorder : IDisposable
{
    public void Dispose()
    {
        _listView.RemoveHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPressed));
        _listView.RemoveHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnMoved));
        _listView.RemoveHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnReleased));
        _listView.RemoveHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnCanceled));
        _parentGrid.Children.Remove(_overlay);
    }

    private readonly ListView _listView;
    private readonly Grid _parentGrid;
    private readonly ObservableCollection<HotKeyBindingViewModel> _items;
    private readonly Action _saveItems;
    private readonly Canvas _overlay;

    private const double DragThreshold = 8;
    private const double ScrollEdgeSize = 40;
    private const double ScrollSpeed = 10;

    private bool _isDragging;
    private int _dragIndex = -1;
    private int _originalIndex = -1;
    private Point _dragStart;
    private bool _pointerDown;
    private HotKeyBindingViewModel? _dragItem;

    private Border? _ghost;
    private Border? _insertLine;
    private ScrollViewer? _scrollViewer;
    private double _ghostOffsetX, _ghostOffsetY;
    private double _lastIndicatorY;
    private bool _ghostFadingOut;
    private int _dragGen;

    internal ListViewDragReorder(
        ListView listView,
        ObservableCollection<HotKeyBindingViewModel> items,
        Action saveItems)
    {
        _listView = listView;
        _items = items;
        _saveItems = saveItems;
        _parentGrid = (Grid)listView.Parent;

        _overlay = new Canvas { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        _parentGrid.Children.Add(_overlay);

        _listView.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPressed), true);
        _listView.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnMoved), true);
        _listView.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnReleased), true);
        _listView.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnCanceled), true);
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(_listView);
        if (!pt.Properties.IsLeftButtonPressed) return;
        if (IsInteractive(e.OriginalSource)) return;

        var vm = ItemAt(pt.Position);
        if (vm == null) return;

        _pointerDown = true;
        _dragStart = pt.Position;
        _dragIndex = _items.IndexOf(vm);
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pointerDown) return;
        var pt = e.GetCurrentPoint(_listView);
        if (!pt.Properties.IsLeftButtonPressed) { OnCanceled(sender, e); return; }

        if (_isDragging)
        {
            var gp = e.GetCurrentPoint(_parentGrid);
            Canvas.SetLeft(_ghost!, gp.Position.X - _ghostOffsetX);
            Canvas.SetTop(_ghost!, gp.Position.Y - _ghostOffsetY);

            MoveIndicator(pt.Position.Y);
            AutoScroll(e);
            e.Handled = true;
            return;
        }

        var dx = pt.Position.X - _dragStart.X;
        var dy = pt.Position.Y - _dragStart.Y;
        if (Math.Sqrt(dx * dx + dy * dy) >= DragThreshold)
            BeginDrag(e);
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_pointerDown) return;
        _pointerDown = false;

        if (_isDragging)
        {
            _isDragging = false;
            e.Handled = true;

            // Save drop position before animation starts
            var pt = e.GetCurrentPoint(_listView);
            var finalY = pt.Position.Y;

            AnimateGhostAway(() => FinalizeDrop(finalY));
            return;
        }

        Cleanup();
    }

    private void OnCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging) { Cleanup(); return; }
        _isDragging = false;
        _pointerDown = false;

        AnimateGhostAway(RevertAndCleanup);
    }

    private void RevertAndCleanup()
    {
        if (_originalIndex < 0) return; // Already cleaned up
        RestoreOpacity();
        Cleanup();
    }

    private void FinalizeDrop(double pointerY)
    {
        if (_originalIndex < 0) return; // Guard: Cleanup already ran

        var target = InsertIndex(pointerY);

        if (target != _originalIndex)
            _items.Move(_originalIndex, target);

        RestoreOpacity();
        _saveItems();
        Cleanup();
    }

    private void BeginDrag(PointerRoutedEventArgs e)
    {
        if (_dragIndex < 0) return;

        // Abort any pending fade-out from a previous incomplete drag
        _overlay.Children.Clear();
        _overlay.Visibility = Visibility.Collapsed;
        _ghost = null;
        _insertLine = null;
        _ghostFadingOut = false;
        RestoreOpacity();
        _listView.ReleasePointerCaptures();

        _isDragging = true;
        _dragItem = _items[_dragIndex];
        _originalIndex = _dragIndex;
        _dragIndex = -1;

        _listView.CapturePointer(e.Pointer);
        _dragGen++;

        // Dim original position as placeholder
        DimItem(_originalIndex, 0.2);

        // Build ghost
        _ghost = BuildGhost();
        _overlay.Children.Add(_ghost);

        // Position ghost at original item's location, compute cursor offset
        var container = _listView.ContainerFromIndex(_originalIndex) as ListViewItem;
        var gp = e.GetCurrentPoint(_parentGrid);
        if (container != null)
        {
            var origPos = container.TransformToVisual(_parentGrid).TransformPoint(new Point(0, 0));
            Canvas.SetLeft(_ghost, origPos.X);
            Canvas.SetTop(_ghost, origPos.Y);
            _ghostOffsetX = gp.Position.X - origPos.X;
            _ghostOffsetY = gp.Position.Y - origPos.Y;
        }
        else
        {
            // Fallback: center on cursor
            Canvas.SetLeft(_ghost, gp.Position.X - _listView.ActualWidth / 2);
            Canvas.SetTop(_ghost, gp.Position.Y - 28);
            _ghostOffsetX = _listView.ActualWidth / 2;
            _ghostOffsetY = 28;
        }

        // Build insertion indicator (starts invisible, fades in)
        _insertLine = new Border
        {
            Height = 2,
            Width = _listView.ActualWidth,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xCC, 0xD9, 0x90, 0x4A)),
            CornerRadius = new CornerRadius(1),
            IsHitTestVisible = false,
            Opacity = 0
        };
        Canvas.SetLeft(_insertLine, 0);
        Canvas.SetTop(_insertLine, IndicatorY(gp.Position.Y));
        _overlay.Children.Add(_insertLine);

        _overlay.Visibility = Visibility.Visible;

        // Fade in ghost — from position of original item
        _ghost.Opacity = 0;
        var fadeIn = new DoubleAnimation
        {
            From = 0, To = 0.95,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(fadeIn, _ghost);
        Storyboard.SetTargetProperty(fadeIn, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(fadeIn);
        sb.Begin();

        // Fade in indicator
        var indicatorFade = new DoubleAnimation
        {
            From = 0, To = 1,
            Duration = TimeSpan.FromMilliseconds(150),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(indicatorFade, _insertLine);
        Storyboard.SetTargetProperty(indicatorFade, "Opacity");
        var sb2 = new Storyboard();
        sb2.Children.Add(indicatorFade);
        sb2.Begin();

        // Find ScrollViewer for auto-scroll
        FindScrollViewer();

        // Change cursor to indicate dragging
        var coreWindow = CoreWindow.GetForCurrentThread();
        if (coreWindow != null)
            coreWindow.PointerCursor = new CoreCursor(CoreCursorType.SizeNorthSouth, 0);
    }

    private void AutoScroll(PointerRoutedEventArgs e)
    {
        if (_scrollViewer == null) return;

        var pt = e.GetCurrentPoint(_listView);
        var listHeight = _listView.ActualHeight;

        if (pt.Position.Y < ScrollEdgeSize)
        {
            var newOffset = Math.Max(0, _scrollViewer.VerticalOffset - ScrollSpeed);
            _scrollViewer.ChangeView(null, newOffset, null, true);
        }
        else if (pt.Position.Y > listHeight - ScrollEdgeSize)
        {
            var newOffset = Math.Min(
                _scrollViewer.ScrollableHeight,
                _scrollViewer.VerticalOffset + ScrollSpeed);
            _scrollViewer.ChangeView(null, newOffset, null, true);
        }
    }

    private void FindScrollViewer()
    {
        if (_scrollViewer != null) return;
        _scrollViewer = FindScrollViewerRecursive(_listView);
    }

    private static ScrollViewer? FindScrollViewerRecursive(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer sv) return sv;
            var found = FindScrollViewerRecursive(child);
            if (found != null) return found;
        }
        return null;
    }

    private void AnimateGhostAway(Action onCompleted)
    {
        if (_ghost == null || _ghostFadingOut) { onCompleted(); return; }
        _ghostFadingOut = true;
        var gen = _dragGen;

        var fadeOut = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(150),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(fadeOut, _ghost);
        Storyboard.SetTargetProperty(fadeOut, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(fadeOut);
        sb.Completed += (_, _) =>
        {
            if (gen != _dragGen) return; // New drag started — discard stale callback
            onCompleted();
        };
        sb.Begin();
    }

    private void MoveIndicator(double pointerY)
    {
        if (_insertLine == null) return;
        var y = IndicatorY(pointerY);
        Canvas.SetTop(_insertLine, y);
    }

    private double IndicatorY(double pointerY)
    {
        for (int i = 0; i < _items.Count; i++)
        {
            var c = _listView.ContainerFromIndex(i) as ListViewItem;
            if (c == null) continue;
            var t = c.TransformToVisual(_parentGrid).TransformPoint(new Point(0, 0)).Y;
            var mid = t + c.ActualHeight / 2;
            if (pointerY < mid)
            {
                _lastIndicatorY = t;
                return t;
            }
        }
        // Below last item
        if (_items.Count > 0)
        {
            var c = _listView.ContainerFromIndex(_items.Count - 1) as ListViewItem;
            if (c != null)
            {
                var t = c.TransformToVisual(_parentGrid).TransformPoint(new Point(0, 0)).Y;
                _lastIndicatorY = t + c.ActualHeight;
                return t + c.ActualHeight;
            }
        }
        // Fallback — keep last known position
        return _lastIndicatorY;
    }

    private void RestoreOpacity()
    {
        for (int i = 0; i < _items.Count; i++)
        {
            var c = _listView.ContainerFromIndex(i) as ListViewItem;
            if (c != null) c.Opacity = 1.0;
        }
    }

    private void DimItem(int index, double opacity)
    {
        var c = _listView.ContainerFromIndex(index) as ListViewItem;
        if (c != null) c.Opacity = opacity;
    }

    private void Cleanup()
    {
        _overlay.Visibility = Visibility.Collapsed;
        _overlay.Children.Clear();
        _ghost = null;
        _insertLine = null;

        _listView.ReleasePointerCaptures();

        // Restore default cursor
        var coreWindow = CoreWindow.GetForCurrentThread();
        if (coreWindow != null)
            coreWindow.PointerCursor = new CoreCursor(CoreCursorType.Arrow, 0);

        _isDragging = false;
        _pointerDown = false;
        _dragItem = null;
        _dragIndex = -1;
        _originalIndex = -1;
        _ghostFadingOut = false;
    }

    private int InsertIndex(double pointerY)
    {
        for (int i = 0; i < _items.Count; i++)
        {
            var c = _listView.ContainerFromIndex(i) as ListViewItem;
            if (c == null) continue;
            var t = c.TransformToVisual(_listView).TransformPoint(new Point(0, 0)).Y;
            if (pointerY < t + c.ActualHeight / 2) return i;
        }
        return _items.Count;
    }

    private HotKeyBindingViewModel? ItemAt(Point point)
    {
        for (int i = 0; i < _items.Count; i++)
        {
            var c = _listView.ContainerFromIndex(i) as ListViewItem;
            if (c == null) continue;
            var t = c.TransformToVisual(_listView).TransformPoint(new Point(0, 0)).Y;
            var b = t + c.ActualHeight;
            if (point.Y >= t && point.Y <= b) return _items[i];
        }
        return null;
    }

    private static bool IsInteractive(object s)
    {
        if (s is Button or ToggleSwitch) return true;
        if (s is DependencyObject d)
        {
            for (var p = VisualTreeHelper.GetParent(d); p != null; p = VisualTreeHelper.GetParent(p))
                if (p is Button or ToggleSwitch) return true;
        }
        return false;
    }

    private Border BuildGhost()
    {
        var icon = new Image
        {
            Width = 36, Height = 36,
            Source = _dragItem?.IconSource,
            VerticalAlignment = VerticalAlignment.Center
        };

        var name = new TextBlock
        {
            Text = _dragItem?.Name ?? "",
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var path = new TextBlock
        {
            Text = _dragItem?.AppPath ?? "",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.6,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        };

        var textStack = new StackPanel { Spacing = 2 };
        textStack.Children.Add(name);
        textStack.Children.Add(path);

        var grid = new Grid
        {
            Padding = new Thickness(12, 8, 12, 8),
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(12) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            }
        };
        Grid.SetColumn(icon, 0); grid.Children.Add(icon);
        Grid.SetColumn(textStack, 2); grid.Children.Add(textStack);

        return new Border
        {
            Child = grid,
            Width = _listView.ActualWidth,
            Height = 56,
            Background = Application.Current.Resources["AcrylicInAppFillColorDefaultBrush"] as Brush
                         ?? new SolidColorBrush(Windows.UI.Color.FromArgb(0xE0, 0x2D, 0x2D, 0x2D)),
            CornerRadius = new CornerRadius(10),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Translation = new Vector3(0, 0, 48),
            Opacity = 0.95
        };
    }
}
