using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;
using Windows.System;

namespace WinAssistant.Controls.Tools;

public class MobileHotspotTool : IAssistantTool
{
    public string Id => "mobile-hotspot";
    public string Name => "移动热点";
    public string Description => "一键开启移动热点";
    public string IconGlyph => "📶";
    public string? IconColorHex => "#FF60A5FA";

    public bool IsOneClickAction => true;

    public string? Activate()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var profile = GetConnectionProfile();
                if (profile == null) return;

                var manager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
                _ = await manager.StartTetheringAsync();
            }
            catch { }
        });

        App.DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                _ = await Launcher.LaunchUriAsync(new Uri("ms-settings:network-mobilehotspot"));
            }
            catch { }
        });

        return "正在开启移动热点";
    }

    private static ConnectionProfile? GetConnectionProfile()
    {
        try
        {
            var profiles = NetworkInformation.GetConnectionProfiles();
            return profiles.FirstOrDefault(p =>
                p.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess)
                ?? profiles.FirstOrDefault();
        }
        catch { return null; }
    }

    public (double width, double height) DefaultWindowSize => (320, 200);

    public UIElement CreateContent() =>
        new TextBlock
        {
            Text = "移动热点",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

    public UIElement? CreateSettingsContent() => null;
}
