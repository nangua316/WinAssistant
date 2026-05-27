using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using WinAssistant.Controls.Tools;
using WinAssistant.Helpers;
using WinAssistant.Models;
using WinAssistant.ViewModels;

namespace WinAssistant;

public sealed partial class MainPage : Page
{
    public MainPageViewModel ViewModel { get; }

    public MainPage()
    {
        InitializeComponent();
        ViewModel = App.GetService<MainPageViewModel>();
    }

    private ListViewDragReorder? _reorder;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.LoadSettings();
        _reorder = new ListViewDragReorder(BindingListView, ViewModel.Bindings, ViewModel.SaveSettings);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
    }

    private void OnMenuSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Null guard: this fires during InitializeComponent() before x:Name fields are wired
        if (GeneralPanel == null) return;
        var index = MenuListView.SelectedIndex;
        GeneralPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        LaunchpadPanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        HotkeyPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        ToolPanel.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        AddAppButton.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;

        TitleText.Text = index switch
        {
            0 => "常规设置",
            1 => "启动台设置",
            2 => "全局快捷键管理",
            3 => "小工具",
            _ => ""
        };
        SubtitleText.Text = index switch
        {
            0 => "设置应用程序的基本选项",
            1 => "配置启动台的触发方式和行为",
            2 => "添加应用并设置全局快捷键",
            3 => "管理小工具，添加到启动台快速访问",
            _ => ""
        };

        if (index == 3)
            PopulateToolList();
    }

    private void PopulateToolList()
    {
        ToolListStack.Children.Clear();
        ToolListPanel.Visibility = Visibility.Visible;
        ToolSettingsPanel.Visibility = Visibility.Collapsed;
        _currentToolSettingsTool = null;

        var settings = App.SettingsService.Load();
        var toolIdsInLaunchpad = settings.LaunchpadItems
            .Where(i => i.ToolId != null)
            .Select(i => i.ToolId)
            .ToHashSet();

        foreach (var tool in ToolRegistry.All)
        {
            var isInLaunchpad = toolIdsInLaunchpad.Contains(tool.Id);

            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(0, 0, 0, 4)
            };

            var row = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = GridLength.Auto }
                },
                ColumnSpacing = 14
            };

            // Icon — prefer extracted icon, fall back to glyph
            FrameworkElement icon = CreateToolIcon(tool);
            Grid.SetColumn(icon, 0);
            row.Children.Add(icon);

            // Name + description
            var infoPanel = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 2
            };
            infoPanel.Children.Add(new TextBlock
            {
                Text = tool.Name,
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            infoPanel.Children.Add(new TextBlock
            {
                Text = tool.Description,
                FontSize = 12,
                Opacity = 0.6
            });
            Grid.SetColumn(infoPanel, 1);
            row.Children.Add(infoPanel);

            // Settings button (if tool has settings)
            if (tool.CreateSettingsContent() != null)
            {
                var settingsBtn = new Button
                {
                    Content = new FontIcon { Glyph = "", FontSize = 14 },
                    MinWidth = 36,
                    MinHeight = 36,
                    CornerRadius = new Microsoft.UI.Xaml.CornerRadius(4),
                    Tag = tool,
                    VerticalAlignment = VerticalAlignment.Center
                };
                ToolTipService.SetToolTip(settingsBtn, "工具设置");
                settingsBtn.Click += OnToolSettingsClick;
                Grid.SetColumn(settingsBtn, 2);
                row.Children.Add(settingsBtn);
            }

            // Toggle: show in launchpad
            var toggle = new ToggleSwitch
            {
                IsOn = isInLaunchpad,
                MinWidth = 50,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = tool
            };
            toggle.Toggled += OnToolLaunchpadToggle;
            Grid.SetColumn(toggle, 3);
            row.Children.Add(toggle);

            card.Child = row;
            ToolListStack.Children.Add(card);
        }
    }

    private IAssistantTool? _currentToolSettingsTool;

    private static FrameworkElement CreateToolIcon(IAssistantTool tool)
    {
        var container = new Grid
        {
            Width = 40,
            Height = 40,
            VerticalAlignment = VerticalAlignment.Center
        };

        var extractPath = tool.IconExtractPath;
        if (!string.IsNullOrEmpty(extractPath))
        {
            var cachedIcon = IconHelper.ExtractAppIconToAppData(extractPath, 48);
            if (cachedIcon != null)
            {
                try
                {
                    var img = new Image
                    {
                        Width = 36,
                        Height = 36,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    var bitmap = new BitmapImage { UriSource = new Uri(cachedIcon) };
                    img.Source = bitmap;
                    container.Children.Add(img);
                    return container;
                }
                catch { }
            }
        }

        var iconForeground = tool.IconColorHex is string hex
            ? ParseBrush(hex)
            : new SolidColorBrush(Color.FromArgb(0xFF, 0x60, 0xA5, 0xFA));
        container.Children.Add(new TextBlock
        {
            Text = tool.IconGlyph,
            FontSize = 24,
            Foreground = iconForeground,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        return container;
    }

    private void OnToolLaunchpadToggle(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle) return;
        if (toggle.Tag is not IAssistantTool tool) return;

        var settings = App.SettingsService.Load();

        if (toggle.IsOn)
        {
            // Add to launchpad if not already present
            if (!settings.LaunchpadItems.Any(i => i.ToolId == tool.Id))
            {
                settings.LaunchpadItems.Add(new LaunchpadItem
                {
                    Name = tool.Name,
                    ToolId = tool.Id
                });
            }
        }
        else
        {
            // Remove from launchpad
            settings.LaunchpadItems.RemoveAll(i => i.ToolId == tool.Id);
        }

        App.SettingsService.Save(settings);
    }

    private void OnToolSettingsClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not IAssistantTool tool) return;
        if (tool.CreateSettingsContent() is not UIElement settingsUI) return;

        _currentToolSettingsTool = tool;
        ToolListPanel.Visibility = Visibility.Collapsed;
        ToolSettingsPanel.Visibility = Visibility.Visible;
        ToolSettingsContent.Children.Clear();
        ToolSettingsContent.Children.Add(settingsUI);

        TitleText.Text = $"{tool.Name} 设置";
        SubtitleText.Text = tool.Description;
    }

    private void OnToolSettingsBack(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        _currentToolSettingsTool = null;
        ToolSettingsPanel.Visibility = Visibility.Collapsed;
        ToolListPanel.Visibility = Visibility.Visible;
        TitleText.Text = "小工具";
        SubtitleText.Text = "管理小工具，添加到启动台快速访问";
    }

    private static Brush ParseBrush(string hex)
    {
        try
        {
            hex = hex.TrimStart('#');
            var a = byte.Parse(hex[..2], System.Globalization.NumberStyles.HexNumber);
            var r = byte.Parse(hex[2..4], System.Globalization.NumberStyles.HexNumber);
            var g = byte.Parse(hex[4..6], System.Globalization.NumberStyles.HexNumber);
            var b = byte.Parse(hex[6..8], System.Globalization.NumberStyles.HexNumber);
            return new SolidColorBrush(Color.FromArgb(a, r, g, b));
        }
        catch { return new SolidColorBrush(Color.FromArgb(0xFF, 0x60, 0xA5, 0xFA)); }
    }

    private void OnOpenLaunchpadClick(object sender, RoutedEventArgs e)
    {
        App.LaunchpadWindow.Open();
    }

    private async void OnModifyGlobalHotKeyClick(object sender, RoutedEventArgs e)
    {
        uint capturedMods = 0;
        uint capturedVk = 0;
        var capturedDisplay = "";

        var inputBox = new TextBox
        {
            Text = "按下快捷键组合...",
            FontSize = 28,
            TextAlignment = TextAlignment.Center,
            IsReadOnly = true,
            Width = 320,
            Height = 60,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 30, 0, 30)
        };

        var dialog = new ContentDialog
        {
            Title = "设置全局快捷键",
            Content = inputBox,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };

        inputBox.KeyDown += (s, ke) =>
        {
            var key = ke.Key;

            uint mods = 0;
            if (IsKeyDown(VirtualKey.Control)) mods |= KeyHelper.MOD_CONTROL;
            if (IsKeyDown(VirtualKey.Menu)) mods |= KeyHelper.MOD_ALT;
            if (IsKeyDown(VirtualKey.Shift)) mods |= KeyHelper.MOD_SHIFT;
            if (IsKeyDown(VirtualKey.LeftWindows) ||
                IsKeyDown(VirtualKey.RightWindows))
                mods |= KeyHelper.MOD_WIN;

            bool isMod = key is VirtualKey.Control
                or VirtualKey.Menu
                or VirtualKey.Shift
                or VirtualKey.LeftWindows
                or VirtualKey.RightWindows;

            if (isMod)
            {
                inputBox.Text = mods > 0
                    ? $"{KeyHelper.GetModifierDisplay(mods)} + ..."
                    : "按下快捷键组合...";
                ke.Handled = true;
                return;
            }

            if (mods == 0)
            {
                inputBox.Text = "请至少包含一个修饰键 (Ctrl/Alt/Shift/Win)";
                ke.Handled = true;
                return;
            }

            capturedMods = mods;
            capturedVk = (uint)key;
            capturedDisplay = KeyHelper.GetFullDisplay(mods, (uint)key);
            inputBox.Text = capturedDisplay;
            ke.Handled = true;
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && capturedMods > 0 && capturedVk > 0)
        {
            ViewModel.SetLaunchpadHotKey(capturedMods, capturedVk, capturedDisplay);
        }
    }

    private static bool IsKeyDown(VirtualKey key)
    {
        return InputKeyboardSource.GetKeyStateForCurrentThread(key)
            .HasFlag(CoreVirtualKeyStates.Down);
    }

    #region Hotkey list event handlers

    private bool _toggling;

    private async void OnToggleToggled(object sender, RoutedEventArgs e)
    {
        if (_toggling) return;
        if (sender is not ToggleSwitch toggle) return;
        if (toggle.Tag is not HotKeyBindingViewModel vm) return;

        _toggling = true;

        if (toggle.IsOn && vm.Model.Modifiers != 0 && vm.Model.VirtualKey != 0)
        {
            var conflict = ViewModel.FindBindingConflict(vm.Model.Modifiers, vm.Model.VirtualKey, vm);
            if (conflict != null)
            {
                toggle.IsOn = false;
                _toggling = false;
                _ = new ContentDialog
                {
                    Title = "快捷键冲突",
                    Content = $"无法启用：快捷键 {vm.Model.HotKeyDisplay} 已被 \"{conflict.Name}\" 使用",
                    CloseButtonText = "确定",
                    XamlRoot = this.XamlRoot
                }.ShowAsync();
                return;
            }
        }

        ViewModel.ToggleBindingCommand.Execute(vm);
        toggle.IsOn = vm.IsEnabled;
        _toggling = false;
    }

    private async void OnIconImageLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Image image) return;
        if (image.Tag is not HotKeyBindingViewModel vm) return;
        if (vm.IconSource != null) return;

        var tempFile = await Task.Run(() =>
            IconHelper.ExtractAppIconToAppData(vm.AppPath, aumid: vm.Model.Aumid));
        if (tempFile == null) return;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.UriSource = new Uri(tempFile);
            vm.IconSource = bitmap;
        }
        catch { }
    }

    #endregion

}
