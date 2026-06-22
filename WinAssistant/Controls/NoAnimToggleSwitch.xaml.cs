using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace WinAssistant.Controls;

/// <summary>
/// 开关控件——外观与原生 ToggleSwitch 一致（40x20 轨道、12x12 滑块），
/// 点击时有平滑过渡动画，程序化设置状态时无动画，
/// 彻底避免拖拽/初始化时的闪烁问题。
/// </summary>
public sealed partial class NoAnimToggleSwitch : UserControl
{
    private bool _suppressCallback;
    private Storyboard? _currentAnimation;

    public NoAnimToggleSwitch()
    {
        InitializeComponent();
        ApplyVisualState(false);
        PointerPressed += OnPressed;
    }

    public bool IsOn
    {
        get => (bool)GetValue(IsOnProperty);
        set => SetValue(IsOnProperty, value);
    }

    public static readonly DependencyProperty IsOnProperty =
        DependencyProperty.Register(nameof(IsOn), typeof(bool), typeof(NoAnimToggleSwitch),
            new PropertyMetadata(false, (d, e) =>
            {
                if (d is NoAnimToggleSwitch self && !self._suppressCallback)
                    self.ApplyVisualState((bool)e.NewValue);
            }));

    public event RoutedEventHandler? Toggled;

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        _suppressCallback = true;
        var targetOn = !IsOn;

        _currentAnimation?.Stop();
        _currentAnimation = AnimateTo(targetOn);

        IsOn = targetOn;
        _suppressCallback = false;
        Toggled?.Invoke(this, e);
    }

    /// <summary>
    /// 从当前视觉状态动画过渡到目标状态。
    /// 显式设置 From 确保无论之前是否被中断，起点始终正确。
    /// </summary>
    private Storyboard AnimateTo(bool targetOn)
    {
        var sb = new Storyboard();
        var duration = new Duration(TimeSpan.FromMilliseconds(200));
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

        if (targetOn)
        {
            // OFF → ON
            AddAnim(sb, _trackOn, "Opacity", 0, 1, duration, easing);
            AddAnim(sb, _thumbOff, "Opacity", 1, 0, duration, null);
            AddAnim(sb, _thumbOn, "Opacity", 0, 1, duration, null);
            AddAnim(sb, _thumbTranslate, "X", 0, 20, duration, easing);
        }
        else
        {
            // ON → OFF
            AddAnim(sb, _trackOn, "Opacity", 1, 0, duration, easing);
            AddAnim(sb, _thumbOff, "Opacity", 0, 1, duration, null);
            AddAnim(sb, _thumbOn, "Opacity", 1, 0, duration, null);
            AddAnim(sb, _thumbTranslate, "X", 20, 0, duration, easing);
        }

        sb.Begin();
        return sb;
    }

    private void ApplyVisualState(bool isOn)
    {
        _currentAnimation?.Stop();
        _currentAnimation = null;
        _trackOn.Opacity = isOn ? 1 : 0;
        _thumbOff.Opacity = isOn ? 0 : 1;
        _thumbOn.Opacity = isOn ? 1 : 0;
        _thumbTranslate.X = isOn ? 20 : 0;
    }

    private static void AddAnim(Storyboard sb, DependencyObject target, string property,
        double from, double to, Duration duration, EasingFunctionBase? easing)
    {
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = duration,
            EasingFunction = easing
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, property);
        sb.Children.Add(anim);
    }
}
