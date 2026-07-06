using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using WinAssistant.Controls;
using WinAssistant.Controls.AiChat;
using WinAssistant.Controls.Tools;
using WinAssistant.Helpers;
using WinAssistant.Models;
using WinAssistant.Services;
using WinAssistant.ViewModels;

namespace WinAssistant;

public sealed partial class MainPage : Page
{
    public MainPageViewModel ViewModel { get; }

    /// <summary>Try to load a system Brush; fallback to an app-level Brush if unavailable.</summary>
    private static Brush SysBrush(string resourceKey, string fallbackKey)
    {
        if (Application.Current.Resources.TryGetValue(resourceKey, out object v) && v is Brush b)
            return b;
        if (Application.Current.Resources.TryGetValue(fallbackKey, out object v2) && v2 is Brush fb)
            return fb;
        return new SolidColorBrush(Colors.Gray);
    }

    public MainPage()
    {
        // 先在 InitializeComponent 前设置 ViewModel，确保 x:Bind 能正确解析初始值
        ViewModel = App.GetService<MainPageViewModel>();
        // RequestedTheme 也在 InitializeComponent 前设置，确保 ThemeResource 用目标主题
        this.RequestedTheme = App.CurrentTheme == ApplicationTheme.Light
            ? ElementTheme.Light : ElementTheme.Dark;
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // 主题变化时更新本页面的 RequestedTheme
        App.SystemThemeChanged += OnSystemThemeChanged;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        App.SystemThemeChanged -= OnSystemThemeChanged;
    }

    private void OnSystemThemeChanged(object? sender, EventArgs e)
    {
        this.RequestedTheme = App.CurrentTheme == ApplicationTheme.Light
            ? ElementTheme.Light : ElementTheme.Dark;
    }

    private ListViewDragReorder? _reorder;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.LoadSettings();
        _reorder = new ListViewDragReorder(BindingListView, ViewModel.Bindings, ViewModel.SaveSettings);

        var ver = typeof(App).Assembly.GetName().Version;
        VersionText.Text = ver != null ? $"版本 {ver.Major}.{ver.Minor}.{ver.Build}" : "";

        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModel.UpdateAvailableVersion))
                UpdateText.Text = ViewModel.UpdateAvailableVersion != null
                    ? $"发现新版本 {ViewModel.UpdateAvailableVersion} →"
                    : "";
        };

        // 预填充工具列表（此时窗口在 DWM Cloak 中，ToggleSwitch 的初始动画不被用户看到）
        PopulateToolList();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _reorder?.Dispose();
        _reorder = null;
    }

    private void OnMenuSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Null guard: this fires during InitializeComponent() before x:Name fields are wired
        if (GeneralPanel == null) return;
        var index = MenuListView.SelectedIndex;
        GeneralPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        HotkeyPanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        AIPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        ToolPanel.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;
        TitleText.Text = index switch
        {
            0 => "常规设置",
            1 => "全局快捷键",
            2 => "AI 技能",
            3 => "小工具",
            4 => "关于",
            _ => ""
        };
        SubtitleText.Text = index switch
        {
            0 => "设置应用程序的基本选项",
            1 => "添加应用并设置全局快捷键",
            2 => "配置 AI 并管理已创建的技能",
            3 => "管理小工具，添加到启动台快速访问",
            4 => "版本信息和项目链接",
            _ => ""
        };

        if (index == 2)
            PopulateAISettings();
    }

    private bool _toolsPopulated;

    private void PopulateToolList()
    {
        if (_toolsPopulated)
        {
            // 后续调用仅切换面板可见性，不重建控件
            ToolListPanel.Visibility = Visibility.Visible;
            ToolSettingsPanel.Visibility = Visibility.Collapsed;
            _currentToolSettingsTool = null;
            return;
        }
        _toolsPopulated = true;

        // 临时让面板可见，使 ToggleSwitch 进入视觉树完成初始化动画
        // （此时窗口在 DWM Cloak 中，用户看不到任何闪烁）
        ToolPanel.Visibility = Visibility.Visible;

        ToolListStack.Children.Clear();
        ToolListPanel.Visibility = Visibility.Visible;
        ToolSettingsPanel.Visibility = Visibility.Collapsed;
        _currentToolSettingsTool = null;

        var settings = ViewModel.Settings;
        var toolIdsInLaunchpad = settings.LaunchpadItems
            .Where(i => i.ToolId != null)
            .Select(i => i.ToolId)
            .ToHashSet();

        foreach (var tool in ToolRegistry.All)
        {
            var isInLaunchpad = toolIdsInLaunchpad.Contains(tool.Id);

            var card = new Border
            {
                Style = (Style)Resources["ToolCardBorderStyle"]
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
            var toggle = new Controls.NoAnimToggleSwitch
            {
                IsOn = isInLaunchpad,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = tool
            };
            toggle.Toggled += OnToolLaunchpadToggle;
            Grid.SetColumn(toggle, 3);
            row.Children.Add(toggle);

            card.Child = row;
            ToolListStack.Children.Add(card);
        }

        // 预填充完成后恢复面板隐藏
        ToolPanel.Visibility = Visibility.Collapsed;
    }

    #region AI 技能设置

    private bool _aiSettingsLoaded;

    private void PopulateAISettings()
    {
        if (_aiSettingsLoaded) return;
        _aiSettingsLoaded = true;

        PopulateSkillList();
    }

    private void PopulateSkillList()
    {
        SkillListStack.Children.Clear();

        var skills = App.SkillLibraryService.AllSkills;
        if (skills.Count == 0)
        {
            SkillListStack.Children.Add(new TextBlock
            {
                Text = "暂无技能，在 AI 对话中发送指令后将自动创建",
                FontSize = 13,
                Opacity = 0.5,
                Margin = new Thickness(0, 8, 0, 0)
            });
            return;
        }

        foreach (var skill in skills)
        {
            var card = new Border
            {
                Style = (Style)Resources["SkillCardBorderStyle"]
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
                ColumnSpacing = 10
            };

            // Icon
            row.Children.Add(new TextBlock
            {
                Text = skill.IconGlyph,
                FontSize = 24,
                VerticalAlignment = VerticalAlignment.Center
            });

            // Info
            var info = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 2
            };
            info.Children.Add(new TextBlock
            {
                Text = skill.Name,
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = SysBrush("SystemControlPageTextBaseHighBrush", "TextPrimaryBrush")
            });
            info.Children.Add(new TextBlock
            {
                Text = $"{skill.ActionType} · {skill.CreatedAt:MM-dd} 创建",
                FontSize = 11,
                Opacity = 0.5
            });
            Grid.SetColumn(info, 1);
            row.Children.Add(info);

            // Edit button
            var editBtn = new Button
            {
                Content = "编辑",
                MinWidth = 50,
                MinHeight = 30,
                CornerRadius = new CornerRadius(4),
                FontSize = 12,
                Padding = new Thickness(8, 2, 8, 2),
                Tag = skill,
                VerticalAlignment = VerticalAlignment.Center
            };
            editBtn.Click += OnEditSkillClick;
            Grid.SetColumn(editBtn, 2);
            row.Children.Add(editBtn);

            // Delete button
            var delBtn = new Button
            {
                Content = new FontIcon { Glyph = "", FontSize = 14 },
                Style = (Style)App.Current.Resources["DeleteIconButtonStyle"],
                MinWidth = 32,
                MinHeight = 32,
                Tag = skill,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTipService.SetToolTip(delBtn, "删除");
            delBtn.Click += OnDeleteSkillClick;
            Grid.SetColumn(delBtn, 3);
            row.Children.Add(delBtn);

            card.Child = row;
            SkillListStack.Children.Add(card);
        }
    }

    private async void OnEditSkillClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not SkillDefinition skill) return;

        var nameBox = new TextBox
        {
            Text = skill.Name,
            Header = "技能名称",
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 8)
        };
        var descBox = new TextBox
        {
            Text = skill.Description,
            Header = "描述",
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 8),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap
        };
        var typeCombo = new ComboBox
        {
            Header = "动作类型",
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 8)
        };
        foreach (var t in Enum.GetValues<SkillActionType>())
            typeCombo.Items.Add(t.ToString());
        typeCombo.SelectedItem = skill.ActionType.ToString();

        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(nameBox);
        stack.Children.Add(descBox);
        stack.Children.Add(typeCombo);

        var dialog = new ContentDialog
        {
            Title = "编辑技能",
            Content = stack,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        if (await SafeShowDialog(dialog) == ContentDialogResult.Primary)
        {
            skill.Name = nameBox.Text.Trim();
            skill.Description = descBox.Text.Trim();
            if (Enum.TryParse<SkillActionType>(typeCombo.SelectedItem?.ToString(), out var newType))
                skill.ActionType = newType;
            App.SkillLibraryService.Update(skill);
            PopulateSkillList();
        }
    }

    private async void OnDeleteSkillClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not SkillDefinition skill) return;

        var dialog = new ContentDialog
        {
            Title = "删除技能",
            Content = $"确定要删除技能「{skill.Name}」吗？",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        if (await SafeShowDialog(dialog) == ContentDialogResult.Primary)
        {
            App.SkillLibraryService.Delete(skill.Id);
            PopulateSkillList();
        }
    }

    private async void OnClearAllSkills(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "清空所有技能",
            Content = "确定要删除所有已创建的技能吗？此操作不可撤销。",
            PrimaryButtonText = "清空",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        if (await SafeShowDialog(dialog) == ContentDialogResult.Primary)
        {
            var skills = App.SkillLibraryService.AllSkills.ToList();
            foreach (var s in skills)
                App.SkillLibraryService.Delete(s.Id);
            PopulateSkillList();
        }
    }

    #endregion

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
        if (sender is not Controls.NoAnimToggleSwitch toggle) return;
        if (toggle.Tag is not IAssistantTool tool) return;

        var settings = ViewModel.Settings;

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
            byte a, r, g, b;
            if (hex.Length == 6)
            {
                a = 0xFF;
                r = byte.Parse(hex[..2], System.Globalization.NumberStyles.HexNumber);
                g = byte.Parse(hex[2..4], System.Globalization.NumberStyles.HexNumber);
                b = byte.Parse(hex[4..6], System.Globalization.NumberStyles.HexNumber);
            }
            else
            {
                a = byte.Parse(hex[..2], System.Globalization.NumberStyles.HexNumber);
                r = byte.Parse(hex[2..4], System.Globalization.NumberStyles.HexNumber);
                g = byte.Parse(hex[4..6], System.Globalization.NumberStyles.HexNumber);
                b = byte.Parse(hex[6..8], System.Globalization.NumberStyles.HexNumber);
            }
            return new SolidColorBrush(Color.FromArgb(a, r, g, b));
        }
        catch { return new SolidColorBrush(Color.FromArgb(0xFF, 0x60, 0xA5, 0xFA)); }
    }

    private void OnOpenLaunchpadClick(object sender, RoutedEventArgs e)
    {
        App.LaunchpadWindow.Open();
    }

    private void OnUpdateTextTapped(object sender, RoutedEventArgs e) =>
        ViewModel.OpenUpdatePage();

    private void OnUpdateTextPointerEntered(object sender, PointerRoutedEventArgs e) =>
        ((TextBlock)sender).Foreground = new SolidColorBrush(Colors.White);

    private void OnUpdateTextPointerExited(object sender, PointerRoutedEventArgs e) =>
        ((TextBlock)sender).Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xD9, 0x90, 0x4A));

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

        var result = await SafeShowDialog(dialog);
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

    /// <summary>
    /// 安全地显示 ContentDialog，如果已有对话框打开则捕获异常不崩溃。
    /// </summary>
    private static async Task<ContentDialogResult> SafeShowDialog(ContentDialog dialog)
    {
        try { return await dialog.ShowAsync(); }
        catch (Exception ex)
        {
            Logger.Log("UI", $"ContentDialog 嵌套冲突: {ex.Message}");
            return ContentDialogResult.None;
        }
    }

    private bool _toggling;

    private async void OnToggleToggled(object sender, RoutedEventArgs e)
    {
        if (_toggling) return;
        if (sender is not Controls.NoAnimToggleSwitch nat) return;
        if (nat.Tag is not HotKeyBindingViewModel vm) return;

        _toggling = true;

        if (nat.IsOn && vm.Model.Modifiers != 0 && vm.Model.VirtualKey != 0)
        {
            var conflict = ViewModel.FindBindingConflict(vm.Model.Modifiers, vm.Model.VirtualKey, vm);
            if (conflict != null)
            {
                nat.IsOn = false;
                _toggling = false;
                try
                {
                    await new ContentDialog
                    {
                        Title = "快捷键冲突",
                        Content = $"无法启用：快捷键 {vm.Model.HotKeyDisplay} 已被 \"{conflict.Name}\" 使用",
                        CloseButtonText = "确定",
                        XamlRoot = this.XamlRoot
                    }.ShowAsync();
                }
                catch (Exception ex)
                {
                    Logger.Log("UI", $"ContentDialog 冲突: {ex.Message}");
                }
                return;
            }
        }

        ViewModel.ToggleBindingCommand.Execute(vm);
        // IsOn 已在 OnPressed 中设置，无需重复赋值（否则会打断动画）
        _toggling = false;
    }

    private async void OnIconImageLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Image image) return;
        if (image.Tag is not HotKeyBindingViewModel vm) return;
        if (vm.IconSource != null) return;

        var tempFile = await Task.Run(() =>
            IconHelper.ExtractAppIconToAppData(vm.Model.IconPath ?? vm.AppPath, aumid: vm.Model.Aumid));
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
