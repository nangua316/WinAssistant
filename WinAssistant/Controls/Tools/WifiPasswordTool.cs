using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace WinAssistant.Controls.Tools;

public class WifiPasswordTool : IAssistantTool
{
    public string Id => "wifi-password";
    public string Name => "WiFi密码";
    public string Description => "查看当前连接的 WiFi 密码";
    public string IconGlyph => "🔑";
    public string? IconColorHex => "#FF60A5FA";

    public bool IsOneClickAction => false;

    public string? Activate() => null;

    public (double width, double height) DefaultWindowSize => (800, 620);

    public UIElement CreateContent()
    {
        var info = GetCurrentWifiInfo();

        var root = new Grid { Margin = new Thickness(48) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 0: label
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 1: ssid
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 2: label
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 3: password
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 4: button

        if (info == null)
        {
            root.Children.Add(new TextBlock
            {
                Text = "未连接到 WiFi",
                FontSize = 16,
                Opacity = 0.6,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            return root;
        }

        var (ssid, password) = info.Value;
        if (string.IsNullOrEmpty(password))
        {
            root.Children.Add(new TextBlock
            {
                Text = "无法获取密码，请确认是否已连接 WiFi。",
                FontSize = 16,
                Opacity = 0.6,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            return root;
        }

        // WiFi name
        var ssidLabel = new TextBlock
        {
            Text = "WiFi 名称",
            FontSize = 13,
            Opacity = 0.5,
            Margin = new Thickness(0, 0, 0, 2)
        };
        Grid.SetRow(ssidLabel, 0);
        root.Children.Add(ssidLabel);

        var ssidBlock = new TextBlock
        {
            Text = ssid,
            FontSize = 24,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 16)
        };
        Grid.SetRow(ssidBlock, 1);
        root.Children.Add(ssidBlock);

        // Password
        var pwdLabel = new TextBlock
        {
            Text = "WiFi 密码",
            FontSize = 13,
            Opacity = 0.5,
            Margin = new Thickness(0, 0, 0, 2)
        };
        Grid.SetRow(pwdLabel, 2);
        root.Children.Add(pwdLabel);

        var passwordBlock = new TextBlock
        {
            Text = password,
            FontSize = 36,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 24)
        };
        Grid.SetRow(passwordBlock, 3);
        root.Children.Add(passwordBlock);

        // Copy button
        var copyBtn = new Button
        {
            Content = "复制 WiFi 名称和密码",
            MinHeight = 42,
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0x60, 0xA5, 0xFA)),
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xF0, 0xF0, 0xF5))
        };
        copyBtn.Click += (_, _) =>
        {
            var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
            data.SetText($"WiFi：{ssid}\n密码：{password}");
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
            Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
            ToolHostWindow.CloseById(Id);
        };
        Grid.SetRow(copyBtn, 4);
        root.Children.Add(copyBtn);

        return root;
    }

    public UIElement? CreateSettingsContent() => null;

    private static (string ssid, string password)? GetCurrentWifiInfo()
    {
        try
        {
            var ssid = RunNetshAndParse("wlan show interfaces", line =>
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("SSID", StringComparison.OrdinalIgnoreCase)
                    && !trimmed.Contains("BSSID", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = trimmed.Split(':');
                    return parts.Length > 1 ? parts[1].Trim() : null;
                }
                return null;
            });

            if (string.IsNullOrEmpty(ssid) || ssid.Equals("", StringComparison.OrdinalIgnoreCase))
                return null;

            var password = RunNetshAndParse(
                $"wlan show profile name=\"{ssid}\" key=clear", line =>
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("Key Content", StringComparison.OrdinalIgnoreCase)
                        || trimmed.StartsWith("关键内容", StringComparison.OrdinalIgnoreCase))
                    {
                        var sep = trimmed.Contains('：') ? '：' : ':';
                        var idx = trimmed.IndexOf(sep);
                        return idx > 0 ? trimmed[(idx + 1)..].Trim() : null;
                    }
                    return null;
                });

            return (ssid, password ?? "");
        }
        catch
        {
            return null;
        }
    }

    private static string? RunNetshAndParse(string arguments, Func<string, string?> parser)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            }
        };
        proc.Start();
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(5000);

        foreach (var line in output.Split('\n', '\r'))
        {
            var result = parser(line);
            if (result != null)
                return result;
        }
        return null;
    }
}
