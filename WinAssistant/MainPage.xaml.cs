using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Microsoft.UI; // Added for theme-aware colors
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

        var ver = typeof(App).Assembly.GetName().Version;
        VersionText.Text = ver != null ? $"版本 {ver.Major}.{ver.Minor}.{ver.Build}" : "";
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
        HotkeyPanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        AIPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        ToolPanel.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        ImePanel.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility = index == 5 ? Visibility.Visible : Visibility.Collapsed;
        AddAppButton.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;

        TitleText.Text = index switch
        {
            0 => "常规设置",
            1 => "全局快捷键管理",
            2 => "AI 技能",
            3 => "小工具",
            4 => "输入法状态管理",
            5 => "关于",
            _ => ""
        };
        SubtitleText.Text = index switch
        {
            0 => "设置应用程序的基本选项",
            1 => "添加应用并设置全局快捷键",
            2 => "配置 AI 并管理已创建的技能",
            3 => "管理小工具，添加到启动台快速访问",
            4 => "管理输入法自动切换规则和查看当前状态",
            5 => "版本信息和项目链接",
            _ => ""
        };

        if (index == 2)
            PopulateAISettings();
        else if (index == 3)
            PopulateToolList();
        else if (index == 4)
            PopulateImePanel();
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

    #region 输入法状态管理

    private void PopulateImePanel()
    {
        var settings = App.SettingsService.Load();
        ImeToastToggle.IsOn = settings.IsImeToastEnabled;
        ImeAutoSwitchToggle.IsOn = settings.IsImeAutoSwitchEnabled;
        ShowImeCurrentStatus();
        PopulateImeRules();
    }

    private void ShowImeCurrentStatus()
    {
        try
        {
            var info = App.ImeService.GetCurrentStatus();
            if (info.Error != null)
            {
                ImeStatusWindow.Text = "当前窗口: 获取失败";
                ImeStatusLayout.Text = $"当前输入法: {info.Error}";
                ImeStatusLang.Text = "中/英: —";
                ImeStatusWidth.Text = "全/半角: —";
                ImeStatusCaps.Text = "大小写: —";
                return;
            }

            ImeStatusWindow.Text = $"当前窗口: {info.ProcessName} — {info.WindowTitle.Truncate(40)}";
            ImeStatusLayout.Text = $"当前输入法: {info.ImeDisplayName} ({info.Klid})";
            ImeStatusLang.Text = $"中/英: {(info.IsChineseMode ? "🇨🇳 中文" : "🇺🇸 英文")}";
            ImeStatusWidth.Text = $"全/半角: {(info.IsFullWidth ? "全角" : "半角")}";
            ImeStatusCaps.Text = $"大小写: {(info.IsCapsLock ? "🔒 开启" : "关闭")}";

            // Show matched rule
            var matchedRule = ImeService.GetMatchingRule(info.ProcessName);
            ImeStatusMatched.Text = matchedRule != null
                ? $"匹配规则: {matchedRule.DisplayName} ({matchedRule.ProcessName})"
                : "匹配规则: 无";
        }
        catch (Exception ex)
        {
            ImeStatusWindow.Text = $"当前窗口: 错误 — {ex.Message}";
        }
    }

    private void PopulateImeRules()
    {
        var settings = App.SettingsService.Load();
        var rules = settings.ImeRules;

        if (rules.Count == 0)
        {
            ImeEmptyState.Visibility = Visibility.Visible;
            ImeRuleListControl.Visibility = Visibility.Collapsed;
            return;
        }

        ImeEmptyState.Visibility = Visibility.Collapsed;
        ImeRuleListControl.Visibility = Visibility.Visible;
        ImeRuleListControl.ItemsSource = rules.ToList(); // ToList() to snapshot
    }

    private static void SaveAndReloadImeRules()
    {
        App.SettingsService.Save(App.SettingsService.Load());
        App.ImeService.ReloadRules();
    }

    private void OnImeToastToggled(object sender, RoutedEventArgs e)
    {
        var settings = App.SettingsService.Load();
        settings.IsImeToastEnabled = ImeToastToggle.IsOn;
        App.SettingsService.Save(settings);
    }

    private void OnImeAutoSwitchToggled(object sender, RoutedEventArgs e)
    {
        var settings = App.SettingsService.Load();
        settings.IsImeAutoSwitchEnabled = ImeAutoSwitchToggle.IsOn;
        App.SettingsService.Save(settings);
    }

    private void OnImeRefreshStatus(object sender, RoutedEventArgs e)
    {
        ShowImeCurrentStatus();
    }

    #region Rule reorder & apply

    private void OnImeRuleMoveUp(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not ImeRule rule) return;
        var settings = App.SettingsService.Load();
        var idx = settings.ImeRules.FindIndex(r => r.Id == rule.Id);
        if (idx <= 0) return;
        (settings.ImeRules[idx], settings.ImeRules[idx - 1]) = (settings.ImeRules[idx - 1], settings.ImeRules[idx]);
        SaveAndReloadImeRules();
        PopulateImeRules();
    }

    private void OnImeRuleMoveDown(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not ImeRule rule) return;
        var settings = App.SettingsService.Load();
        var idx = settings.ImeRules.FindIndex(r => r.Id == rule.Id);
        if (idx < 0 || idx >= settings.ImeRules.Count - 1) return;
        (settings.ImeRules[idx], settings.ImeRules[idx + 1]) = (settings.ImeRules[idx + 1], settings.ImeRules[idx]);
        SaveAndReloadImeRules();
        PopulateImeRules();
    }

    private void OnImeApplyRuleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not ImeRule rule) return;
        var hwnd = ImeService.GetForegroundHwnd();
        if (hwnd == nint.Zero) return;
        var settings = App.SettingsService.Load();
        var targetRule = settings.ImeRules.FirstOrDefault(r => r.Id == rule.Id);
        if (targetRule != null)
            App.ImeService.ApplySpecificRule(targetRule, hwnd);
    }

    #endregion

    private async void OnImeAddRuleClick(object sender, RoutedEventArgs e)
    {
        // Build the add/edit dialog
        var processBox = new TextBox
        {
            Header = "进程名",
            PlaceholderText = "例如: WeChat.exe",
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 6)
        };
        var pickBtn = new Button
        {
            Content = "从运行中程序选取",
            FontSize = 12,
            Padding = new Thickness(8, 2, 8, 2),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 0, 0, 6),
            HorizontalAlignment = HorizontalAlignment.Left
        };

        var displayBox = new TextBox
        {
            Header = "显示名称",
            PlaceholderText = "例如: 微信",
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 6)
        };

        // IME picker
        var imeLabel = new TextBlock
        {
            Text = "键盘布局 / 输入法",
            FontSize = 13,
            Foreground = (Brush)Resources["TextSecondaryBrush"],
            Margin = new Thickness(0, 0, 0, 4)
        };
        var imeCombo = new ComboBox
        {
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 6),
            MinWidth = 280
        };
        var layouts = ImeService.EnumerateKeyboardLayouts();
        foreach (var (klid, name) in layouts)
        {
            imeCombo.Items.Add(new ComboBoxItem
            {
                Content = $"{name} ({klid})",
                Tag = klid
            });
        }
        if (imeCombo.Items.Count > 0)
            imeCombo.SelectedIndex = 0;

        // Language mode
        var langCombo = new ComboBox
        {
            Header = "语言模式",
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 6)
        };
        langCombo.Items.Add(new ComboBoxItem { Content = "🇨🇳 中文", Tag = false });
        langCombo.Items.Add(new ComboBoxItem { Content = "🇺🇸 英文", Tag = true });
        langCombo.SelectedIndex = 0;

        // Full-width
        var widthCombo = new ComboBox
        {
            Header = "标点符号",
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 6)
        };
        widthCombo.Items.Add(new ComboBoxItem { Content = "半角", Tag = false });
        widthCombo.Items.Add(new ComboBoxItem { Content = "全角", Tag = true });
        widthCombo.SelectedIndex = 0;

        // CapsLock
        var capsCombo = new ComboBox
        {
            Header = "大小写",
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 6)
        };
        capsCombo.Items.Add(new ComboBoxItem { Content = "关闭", Tag = false });
        capsCombo.Items.Add(new ComboBoxItem { Content = "开启", Tag = true });
        capsCombo.SelectedIndex = 0;

        var dialogStack = new StackPanel { Spacing = 2, MaxWidth = 400 };
        dialogStack.Children.Add(processBox);
        dialogStack.Children.Add(pickBtn);
        dialogStack.Children.Add(displayBox);
        dialogStack.Children.Add(imeLabel);
        dialogStack.Children.Add(imeCombo);
        dialogStack.Children.Add(langCombo);
        dialogStack.Children.Add(widthCombo);
        dialogStack.Children.Add(capsCombo);

        // Process picker
        pickBtn.Click += async (s2, e2) =>
        {
            var processes = ImeService.EnumRunningProcesses();
            if (processes.Count == 0)
            {
                processBox.Text = "";
                return;
            }

            var pickerStack = new StackPanel { Spacing = 4 };
            var searchBox = new TextBox
            {
                PlaceholderText = "搜索进程...",
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 4)
            };
            pickerStack.Children.Add(searchBox);

            var listBox = new ListBox
            {
                MinHeight = 240, MaxHeight = 360,
                DisplayMemberPath = "Display"
            };

            var items = processes.Select(p => new
            {
                p.ProcessName,
                p.WindowTitle,
                Display = $"{p.ProcessName} — {p.WindowTitle.Truncate(40)}"
            }).ToList();
            listBox.ItemsSource = items;

            searchBox.TextChanged += (s3, e3) =>
            {
                var q = searchBox.Text.Trim();
                listBox.ItemsSource = string.IsNullOrEmpty(q)
                    ? items
                    : items.Where(i =>
                        i.ProcessName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        i.WindowTitle.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            };

            pickerStack.Children.Add(listBox);

            var pickerDialog = new ContentDialog
            {
                Title = "选择运行中的程序",
                Content = pickerStack,
                PrimaryButtonText = "确定",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };

            if (await pickerDialog.ShowAsync() == ContentDialogResult.Primary)
            {
                if (listBox.SelectedItem != null)
                {
                    var selected = (dynamic)listBox.SelectedItem;
                    processBox.Text = selected.ProcessName;
                    displayBox.Text = System.IO.Path.GetFileNameWithoutExtension(selected.ProcessName);
                }
            }
        };

        var dialog = new ContentDialog
        {
            Title = "添加输入法规则",
            Content = dialogStack,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var processName = processBox.Text.Trim();
            if (string.IsNullOrEmpty(processName)) return;

            if (imeCombo.SelectedItem is ComboBoxItem imeItem && imeItem.Tag is string klid)
            {
                var settings = App.SettingsService.Load();

                // Get IME display name
                var imeDisplay = layouts.FirstOrDefault(l => l.Klid == klid).DisplayName;
                if (string.IsNullOrEmpty(imeDisplay)) imeDisplay = klid;

                var rule = new ImeRule
                {
                    ProcessName = processName,
                    DisplayName = string.IsNullOrEmpty(displayBox.Text.Trim())
                        ? Path.GetFileNameWithoutExtension(processName)
                        : displayBox.Text.Trim(),
                    Klid = klid,
                    ImeDisplayName = imeDisplay,
                    UseEnglishMode = langCombo.SelectedItem is ComboBoxItem langItem && (bool)langItem.Tag,
                    UseFullWidth = widthCombo.SelectedItem is ComboBoxItem wItem && (bool)wItem.Tag,
                    CapsLockState = capsCombo.SelectedItem is ComboBoxItem cItem && (bool)cItem.Tag
                };

                settings.ImeRules.Add(rule);
                App.SettingsService.Save(settings);
                PopulateImeRules();
                App.ImeService.ReloadRules();
            }
        }
    }

    private async void OnImeEditRuleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not ImeRule rule) return;

        var processBox = new TextBox
        {
            Text = rule.ProcessName,
            Header = "进程名",
            PlaceholderText = "例如: WeChat.exe",
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 6)
        };
        var displayBox = new TextBox
        {
            Text = rule.DisplayName,
            Header = "显示名称",
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 6)
        };

        var imeCombo = new ComboBox { FontSize = 14, Margin = new Thickness(0, 0, 0, 6), MinWidth = 280 };
        var layouts = ImeService.EnumerateKeyboardLayouts();
        int imeIndex = 0;
        for (int i = 0; i < layouts.Count; i++)
        {
            var (klid, name) = layouts[i];
            var item = new ComboBoxItem { Content = $"{name} ({klid})", Tag = klid };
            imeCombo.Items.Add(item);
            if (klid == rule.Klid) imeIndex = i;
        }
        imeCombo.SelectedIndex = imeIndex;

        var langCombo = new ComboBox
        {
            Header = "语言模式", FontSize = 14, Margin = new Thickness(0, 0, 0, 6)
        };
        langCombo.Items.Add(new ComboBoxItem { Content = "🇨🇳 中文", Tag = false });
        langCombo.Items.Add(new ComboBoxItem { Content = "🇺🇸 英文", Tag = true });
        langCombo.SelectedIndex = rule.UseEnglishMode ? 1 : 0;

        var widthCombo = new ComboBox
        {
            Header = "标点符号", FontSize = 14, Margin = new Thickness(0, 0, 0, 6)
        };
        widthCombo.Items.Add(new ComboBoxItem { Content = "半角", Tag = false });
        widthCombo.Items.Add(new ComboBoxItem { Content = "全角", Tag = true });
        widthCombo.SelectedIndex = rule.UseFullWidth ? 1 : 0;

        var capsCombo = new ComboBox
        {
            Header = "大小写", FontSize = 14, Margin = new Thickness(0, 0, 0, 6)
        };
        capsCombo.Items.Add(new ComboBoxItem { Content = "关闭", Tag = false });
        capsCombo.Items.Add(new ComboBoxItem { Content = "开启", Tag = true });
        capsCombo.SelectedIndex = rule.CapsLockState ? 1 : 0;

        var stack = new StackPanel { Spacing = 2, MaxWidth = 400 };
        stack.Children.Add(processBox);
        stack.Children.Add(displayBox);
        stack.Children.Add(imeCombo);
        stack.Children.Add(langCombo);
        stack.Children.Add(widthCombo);
        stack.Children.Add(capsCombo);

        var dialog = new ContentDialog
        {
            Title = "编辑输入法规则",
            Content = stack,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var processName = processBox.Text.Trim();
            if (string.IsNullOrEmpty(processName)) return;

            rule.ProcessName = processName;
            rule.DisplayName = string.IsNullOrEmpty(displayBox.Text.Trim())
                ? Path.GetFileNameWithoutExtension(processName)
                : displayBox.Text.Trim();

            if (imeCombo.SelectedItem is ComboBoxItem imeItem && imeItem.Tag is string klid)
            {
                rule.Klid = klid;
                rule.ImeDisplayName = layouts.FirstOrDefault(l => l.Klid == klid).DisplayName ?? klid;
            }
            rule.UseEnglishMode = langCombo.SelectedItem is ComboBoxItem langItem && (bool)langItem.Tag;
            rule.UseFullWidth = widthCombo.SelectedItem is ComboBoxItem wItem && (bool)wItem.Tag;
            rule.CapsLockState = capsCombo.SelectedItem is ComboBoxItem cItem && (bool)cItem.Tag;

            var settings = App.SettingsService.Load();
            App.SettingsService.Save(settings);
            PopulateImeRules();
            App.ImeService.ReloadRules();
        }
    }

    private async void OnImeDeleteRuleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not ImeRule rule) return;

        var dialog = new ContentDialog
        {
            Title = "删除规则",
            Content = $"确定要删除「{rule.DisplayName} ({rule.ProcessName})」的输入法规则吗？",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var settings = App.SettingsService.Load();
            settings.ImeRules.RemoveAll(r => r.Id == rule.Id);
            App.SettingsService.Save(settings);
            PopulateImeRules();
            App.ImeService.ReloadRules();
        }
    }

    private void OnImeRuleToggleToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle || toggle.Tag is not ImeRule rule) return;

        rule.IsEnabled = toggle.IsOn;
        var settings = App.SettingsService.Load();
        App.SettingsService.Save(settings);
        App.ImeService.ReloadRules();
    }

    #endregion

    #region AI 技能设置

    private bool _aiSettingsLoaded;

    private void PopulateAISettings()
    {
        if (_aiSettingsLoaded) return;
        _aiSettingsLoaded = true;

        var settings = App.SettingsService.Load();
        AiApiKeyBox.Text = settings.AiApiKey ?? "";
        AiEndpointBox.Text = settings.AiEndpoint;
        var modelIdx = FindModelIndex(settings.AiChatModel);
        AiModelBox.SelectedIndex = modelIdx >= 0 ? modelIdx : 0;

        UpdateAiConfigStatus();
        PopulateSkillList();
    }

    private static int FindModelIndex(string? model)
    {
        var models = new[] { "qwen-plus", "qwen-max", "qwen-turbo", "qwen-long" };
        return Array.IndexOf(models, model);
    }

    private void OnAiConfigChanged(object sender, RoutedEventArgs e)
    {
        var settings = App.SettingsService.Load();
        var changed = false;

        var key = AiApiKeyBox.Text.Trim();
        if (key != (settings.AiApiKey ?? ""))
        {
            settings.AiApiKey = string.IsNullOrEmpty(key) ? null : key;
            changed = true;
        }

        var endpoint = AiEndpointBox.Text.Trim();
        if (endpoint != settings.AiEndpoint && !string.IsNullOrEmpty(endpoint))
        {
            settings.AiEndpoint = endpoint;
            changed = true;
        }

        if (AiModelBox.SelectedItem is ComboBoxItem cbi)
        {
            var model = cbi.Content.ToString() ?? "qwen-plus";
            if (model != settings.AiChatModel)
            {
                settings.AiChatModel = model;
                changed = true;
            }
        }

        if (changed)
        {
            App.SettingsService.Save(settings);
            App.QwenService.Configure(settings.AiApiKey, settings.AiEndpoint, settings.AiChatModel);
            UpdateAiConfigStatus();
        }
    }

    private void UpdateAiConfigStatus()
    {
        if (!App.QwenService.IsConfigured)
        {
            AiConfigStatus.Text = "⚠️ 未配置 API Key，AI 对话功能不可用";
            AiConfigStatus.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xA7, 0x26));
        }
        else
        {
            AiConfigStatus.Text = $"✅ 已配置 · 模型: {App.SettingsService.Load().AiChatModel}";
            AiConfigStatus.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x66, 0xBB, 0x6A));
        }
    }

    private async void OnTestAiConnection(object sender, RoutedEventArgs e)
    {
        var btn = (Button)sender;
        btn.IsEnabled = false;
        btn.Content = "测试中...";

        try
        {
            if (!App.QwenService.IsConfigured)
            {
                AiConfigStatus.Text = "⚠️ 请先填写 API Key";
                AiConfigStatus.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xA7, 0x26));
                return;
            }

            await App.QwenService.ChatAsync("你好，请回复「连接成功」以测试API连通性。");
            AiConfigStatus.Text = $"✅ 连接成功 · 模型: {App.SettingsService.Load().AiChatModel}";
        }
        catch (Exception ex)
        {
            AiConfigStatus.Text = $"❌ 连接失败: {ex.Message}";
            AiConfigStatus.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x44, 0x44));
        }
        finally
        {
            btn.IsEnabled = true;
            btn.Content = "测试连接";
        }
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
                Foreground = (Brush)Resources["TextPrimaryBrush"]
            });
            info.Children.Add(new TextBlock
            {
                Text = $"{skill.ActionType} · 使用 {skill.UsageCount} 次 · {skill.CreatedAt:MM-dd} 创建",
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
                Content = "删除",
                MinWidth = 50,
                MinHeight = 30,
                CornerRadius = new CornerRadius(4),
                FontSize = 12,
                Padding = new Thickness(8, 2, 8, 2),
                Tag = skill,
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0x44, 0x44)),
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x6B, 0x6B))
            };
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

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
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

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
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

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
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
