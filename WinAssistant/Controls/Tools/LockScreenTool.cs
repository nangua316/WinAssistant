using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinAssistant.Controls.Tools;

public class LockScreenTool : IAssistantTool
{
    public string Id => "lock-screen";
    public string Name => "锁定系统";
    public string Description => "锁定电脑，和 Win+L 效果相同";
    public string IconGlyph => "🔒";
    public string? IconColorHex => "#FF60A5FA";

    public bool IsOneClickAction => true;

    public string? Activate()
    {
        LockWorkStation();
        return null; // 锁定成功不需要弹 toast
    }

    public (double width, double height) DefaultWindowSize => (320, 200);

    public UIElement CreateContent() =>
        new TextBlock
        {
            Text = "锁定系统",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

    public UIElement? CreateSettingsContent() => null;

    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();
}
