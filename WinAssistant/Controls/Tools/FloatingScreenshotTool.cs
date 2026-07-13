using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinAssistant.Helpers;
using WinAssistant.ViewModels;

namespace WinAssistant.Controls.Tools;

public class FloatingScreenshotTool : IAssistantTool
{
    public string Id => "floating-screenshot";
    public string Name => "悬浮截图";
    public string Description => "框选截图，图片悬浮显示在桌面，滚轮缩放，双击关闭";
    public string IconGlyph => "✂️";
    public string? IconColorHex => "#FF34D399";
    public bool IsOneClickAction => true;
    public (double width, double height) DefaultWindowSize => (320, 200);

    public UIElement CreateContent() =>
        new TextBlock { Text = "悬浮截图", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    public UIElement? CreateSettingsContent()
    {
        var root = new StackPanel { Spacing = 16 };

        root.Children.Add(new TextBlock
        {
            Text = "快捷键",
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });

        root.Children.Add(new TextBlock
        {
            Text = "设置后可在任何界面按快捷键直接开始悬浮截图。",
            FontSize = 13,
            Opacity = 0.6
        });

        var button = new Button
        {
            MinWidth = 160,
            MinHeight = 40,
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Left
        };

        void UpdateButtonContent()
        {
            var vm = App.GetService<MainPageViewModel>();
            var display = vm.FloatingScreenshotHotKeyDisplay;
            button.Content = string.IsNullOrWhiteSpace(display) ? "点击设置快捷键" : display;
        }

        UpdateButtonContent();

        button.Click += async (s, e) =>
        {
            var xamlRoot = ((Button)s).XamlRoot;
            var (mods, vk, display, confirmed) = await HotKeyCaptureDialog.ShowAsync(xamlRoot, "设置悬浮截图快捷键");
            if (!confirmed) return;

            var vm = App.GetService<MainPageViewModel>();

            // Empty combo means "clear the hotkey" — no conflict check needed.
            if (mods > 0 && vk > 0)
            {
                var conflict = vm.FindFloatingScreenshotHotKeyConflict(mods, vk);
                if (!string.IsNullOrEmpty(conflict))
                {
                    var errorDialog = new ContentDialog
                    {
                        Title = "快捷键冲突",
                        Content = $"该快捷键已被「{conflict}」占用，请换一个。",
                        CloseButtonText = "确定",
                        XamlRoot = xamlRoot
                    };
                    await errorDialog.ShowAsync();
                    return;
                }
            }

            vm.SetFloatingScreenshotHotKey(mods, vk, display);
            UpdateButtonContent();
        };

        root.Children.Add(button);

        return root;
    }

    public string? Activate()
    {
        Logger.Log("FloatingScreenshot", "Activate");
        ScreenshotOverlay.Start();
        return "开始截图";
    }
}
