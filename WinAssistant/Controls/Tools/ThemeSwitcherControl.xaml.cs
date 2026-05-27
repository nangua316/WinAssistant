using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace WinAssistant.Controls.Tools;

public sealed partial class ThemeSwitcherControl : UserControl
{
    private Brush _accentBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x60, 0xA5, 0xFA));
    private Brush _amberBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xA7, 0x26));

    public ThemeSwitcherControl()
    {
        InitializeComponent();
        UpdateUI();
    }

    private void UpdateUI()
    {
        var isLight = ThemeSwitcherTool.IsLightTheme();

        if (isLight)
        {
            ThemeIcon.Text = "☀";
            ThemeIcon.Foreground = _amberBrush;
            StatusText.Text = "当前：浅色模式";
            ToggleButton.Content = "切换到深色模式";
        }
        else
        {
            ThemeIcon.Text = "☽";
            ThemeIcon.Foreground = _accentBrush;
            StatusText.Text = "当前：深色模式";
            ToggleButton.Content = "切换到浅色模式";
        }
    }

    private void OnToggleClick(object sender, RoutedEventArgs e)
    {
        ThemeSwitcherTool.ToggleTheme();
        UpdateUI();
    }
}
