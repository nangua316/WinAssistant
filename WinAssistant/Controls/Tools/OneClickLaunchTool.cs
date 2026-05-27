using System.Linq;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WinAssistant.Controls;
using WinAssistant.Helpers;
using WinAssistant.Models;

namespace WinAssistant.Controls.Tools;

public class OneClickLaunchTool : IAssistantTool
{
    public string Id => "oneclick-launch";
    public string Name => "一键启动";
    public string Description => "一键启动多个应用";
    public string IconGlyph => "🚀";
    public string? IconColorHex => "#FF60A5FA";

    public bool IsOneClickAction => true;

    public string? Activate()
    {
        var apps = LoadApps();
        if (apps.Count == 0)
            return "请先在设置中添加应用";

        _ = Task.Run(() =>
        {
            foreach (var app in apps)
            {
                try
                {
                    AppLauncher.LaunchOrActivate(app.AppPath, "");
                    Thread.Sleep(400);
                }
                catch { }
            }
        });

        return $"正在启动 {apps.Count} 个应用";
    }

    public UIElement? CreateSettingsContent()
    {
        var root = new StackPanel { Spacing = 12 };

        root.Children.Add(new TextBlock
        {
            Text = "管理一键启动的应用列表",
            FontSize = 14,
            Opacity = 0.8
        });

        var listPanel = new StackPanel { Spacing = 4 };
        RefreshList(listPanel);
        root.Children.Add(listPanel);

        var addButton = new Button
        {
            Content = "添加应用",
            MinWidth = 120,
            MinHeight = 36,
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 8, 0, 0)
        };
        addButton.Click += async (s, e) =>
        {
            var existingPaths = new HashSet<string>(LoadApps().Select(a => a.AppPath), StringComparer.OrdinalIgnoreCase);
            var picker = new AppPickerControl();
            picker.SetExistingPaths(existingPaths);

            var dialog = new ContentDialog
            {
                Title = "选择应用程序",
                Content = picker,
                XamlRoot = ((Button)s).XamlRoot
            };

            picker.ItemAdded += item =>
            {
                var apps = LoadApps();
                var finalName = item.Name;
                int suffix = 2;
                while (apps.Any(a => a.Name.Equals(finalName, StringComparison.OrdinalIgnoreCase)))
                    finalName = $"{item.Name} ({suffix++})";

                apps.Add(new LaunchApp { Name = finalName, AppPath = item.AppPath });
                SaveApps(apps);
                RefreshList(listPanel);
            };

            picker.CloseRequested += () => dialog.Hide();

            await dialog.ShowAsync();
        };
        root.Children.Add(addButton);

        return root;
    }

    private void RefreshList(StackPanel panel)
    {
        panel.Children.Clear();
        var apps = LoadApps();

        if (apps.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "还没有添加应用，点击下方按钮添加",
                Opacity = 0.5,
                FontSize = 13
            });
            return;
        }

        foreach (var app in apps)
        {
            var itemRow = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto }
                },
                Padding = new Thickness(8, 6, 8, 6),
                Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
                CornerRadius = new CornerRadius(6)
            };

            var nameBlock = new TextBlock
            {
                Text = app.Name,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 14
            };
            Grid.SetColumn(nameBlock, 0);
            itemRow.Children.Add(nameBlock);

            var removeBtn = new Button
            {
                Content = "✕",
                MinWidth = 28,
                MinHeight = 28,
                CornerRadius = new CornerRadius(4),
                Tag = app.AppPath
            };
            removeBtn.Click += (_, _) =>
            {
                var current = LoadApps();
                current.RemoveAll(a => a.AppPath == (string)((Button)removeBtn).Tag);
                SaveApps(current);
                RefreshList(panel);
            };
            Grid.SetColumn(removeBtn, 1);
            itemRow.Children.Add(removeBtn);

            panel.Children.Add(itemRow);
        }
    }

    private static List<LaunchApp> LoadApps()
    {
        try
        {
            var settings = App.SettingsService.Load();
            if (settings.ToolSettings.TryGetValue("oneclick-launch", out var json) && !string.IsNullOrEmpty(json))
                return JsonSerializer.Deserialize<List<LaunchApp>>(json) ?? [];
        }
        catch { }
        return [];
    }

    private static void SaveApps(List<LaunchApp> apps)
    {
        try
        {
            var settings = App.SettingsService.Load();
            settings.ToolSettings["oneclick-launch"] = JsonSerializer.Serialize(apps);
            App.SettingsService.Save(settings);
        }
        catch { }
    }

    private class LaunchApp
    {
        public string Name { get; set; } = "";
        public string AppPath { get; set; } = "";
    }

    public (double width, double height) DefaultWindowSize => (320, 200);

    public UIElement CreateContent() =>
        new TextBlock
        {
            Text = "一键启动",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
}
