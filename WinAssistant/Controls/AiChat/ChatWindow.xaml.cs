using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;
using WinAssistant.Models;
using WinAssistant.Services;

namespace WinAssistant.Controls.AiChat;

public sealed partial class ChatWindow : Window
{
    private readonly nint _hwnd;
    private readonly QwenService _qwen;
    private readonly SkillLibraryService _library;
    private readonly SkillExecutionService _executor;
    private TextBox _inputBox = null!;
    private Button _sendBtn = null!;
    private bool _sending;

    private static readonly SolidColorBrush TextPrimaryBrush = new(Color.FromArgb(0xFF, 0xF0, 0xF0, 0xF5));
    private static readonly SolidColorBrush TextDimBrush = new(Color.FromArgb(0x60, 0x90, 0x98, 0xA8));
    private static readonly SolidColorBrush AccentBrush = new(Color.FromArgb(0xFF, 0x60, 0xA5, 0xFA));
    private static readonly SolidColorBrush UserBubbleBrush = new(Color.FromArgb(0xFF, 0x60, 0xA5, 0xFA));
    private static readonly SolidColorBrush AsstBubbleBrush = new(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush SurfaceBrush = new(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush SkillTagBrush = new(Color.FromArgb(0x30, 0x60, 0xA5, 0xFA));
    private static readonly SolidColorBrush GreenBrush = new(Color.FromArgb(0xFF, 0x66, 0xBB, 0x6A));

    public ChatWindow()
    {
        InitializeComponent();

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _qwen = App.QwenService;
        _library = App.SkillLibraryService;
        _executor = App.SkillExecutionService;

        BuildTitleBar();
        BuildInputBar();
        SetupWindow();

        AddMessage("assistant", "你好！我是 AI 技能助手。\n\n你可以输入自然语言指令，例如：\n• 帮我打开计算器\n• 开启移动热点\n• 清理系统垃圾\n• 今天天气怎么样");
    }

    private void BuildTitleBar()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new TextBlock { Text = "🤖", FontSize = 20, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(icon, 0);

        var title = new TextBlock
        {
            Text = "AI 技能助手",
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = TextPrimaryBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };
        title.Text += "  —  输入指令执行或创建技能";
        title.FontSize = 12;
        title.Foreground = TextDimBrush;
        title.FontWeight = Microsoft.UI.Text.FontWeights.Normal;

        Grid.SetColumn(title, 1);

        grid.Children.Add(icon);
        grid.Children.Add(title);
        TitleBar.Children.Add(grid);
    }

    private void BuildInputBar()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnSpacing = 8;

        _inputBox = new TextBox
        {
            PlaceholderText = "输入指令，例如：帮我打开计算器...",
            Background = new SolidColorBrush(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)),
            Foreground = TextPrimaryBrush,
            PlaceholderForeground = TextDimBrush,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 10, 14, 10),
            FontSize = 14,
            MinHeight = 40,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = false
        };
        _inputBox.KeyDown += OnInputKeyDown;
        Grid.SetColumn(_inputBox, 0);

        _sendBtn = new Button
        {
            Content = "发送",
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18, 8, 18, 8),
            MinHeight = 40,
            FontSize = 14,
            Background = AccentBrush,
            Foreground = new SolidColorBrush(Colors.White),
            BorderThickness = new Thickness(0)
        };
        _sendBtn.Click += OnSendClick;
        Grid.SetColumn(_sendBtn, 1);

        grid.Children.Add(_inputBox);
        grid.Children.Add(_sendBtn);
        InputBar.Child = grid;
    }

    private void SetupWindow()
    {
        Title = "AI 技能助手";
        ExtendsContentIntoTitleBar = true;

        AppWindow.Resize(new SizeInt32(1600, 1200));

        // Dark mode + rounded corners via DWM
        var darkMode = 1;
        DwmSetWindowAttribute(_hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
        var cornerPref = DWMWCP_ROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));

        var tb = AppWindow.TitleBar;
        tb.ButtonBackgroundColor = Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Colors.Transparent;

        RootGrid.Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_qwen.IsConfigured)
            AddMessage("assistant", "⚠️ 检测到 API 尚未配置，请先在「设置 → AI 技能」中填写 API Key。");

        CenterWindow();
    }

    private void CenterWindow()
    {
        try
        {
            var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
            var work = display.WorkArea;
            var x = work.X + (work.Width - 1600) / 2;
            var y = work.Y + (work.Height - 1200) / 2;
            AppWindow.Move(new PointInt32(x, y));
        }
        catch { }
    }

    private void OnInputKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            SendMessage();
            e.Handled = true;
        }
    }

    private void OnSendClick(object sender, RoutedEventArgs e) => SendMessage();

    private void AddMessage(string role, string content, string? skillName = null)
    {
        var isUser = role == "user";

        // Container
        var container = new StackPanel
        {
            Margin = new Thickness(0, 0, 0, 4),
            MaxWidth = 420,
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left
        };

        // Skill tag (assistant only)
        if (!isUser && skillName != null)
        {
            container.Children.Add(new Border
            {
                Background = SkillTagBrush,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 0, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock
                {
                    Text = $"🔧 {skillName}",
                    FontSize = 11,
                    Foreground = AccentBrush
                }
            });
        }

        // Bubble
        var bubble = new Border
        {
            CornerRadius = new CornerRadius(isUser ? 14 : 14, isUser ? 14 : 14, isUser ? 4 : 14, isUser ? 14 : 4),
            Padding = new Thickness(14, 10, 14, 10),
            Background = isUser ? UserBubbleBrush : AsstBubbleBrush
        };

        bubble.Child = new TextBlock
        {
            Text = content,
            TextWrapping = TextWrapping.WrapWholeWords,
            FontSize = 14,
            LineHeight = 20,
            IsTextSelectionEnabled = true,
            Foreground = isUser
                ? new SolidColorBrush(Colors.White)
                : TextPrimaryBrush
        };

        container.Children.Add(bubble);
        MessageStack.Children.Add(container);

        ScrollToBottom();
    }

    private async void SendMessage()
    {
        var text = _inputBox.Text.Trim();
        if (string.IsNullOrEmpty(text) || _sending) return;

        _inputBox.Text = "";
        _sending = true;
        _sendBtn.IsEnabled = false;

        AddMessage("user", text);

        if (!_qwen.IsConfigured)
        {
            AddMessage("assistant", "⚠️ 请先在「设置 → AI 技能」中配置 API Key 后再使用。");
            _sending = false;
            _sendBtn.IsEnabled = true;
            return;
        }

        try
        {
            AddMessage("assistant", "⏳ 思考中...");

            var result = await _qwen.AnalyzeAsync(text, _library.AllSkills.ToList());

            RemoveLastMessage();

            switch (result.Action)
            {
                case AnalysisAction.Match:
                {
                    var skill = result.SkillId != null ? _library.GetById(result.SkillId) : null;
                    if (skill != null)
                    {
                        _library.IncrementUsage(skill.Id);
                        var execResult = await _executor.ExecuteAsync(skill, text);
                        AddMessage("assistant", $"{result.Reply}\n\n{execResult.Message}", skill.Name);
                    }
                    else
                    {
                        AddMessage("assistant", $"{result.Reply}\n\n✅ 技能已执行");
                    }
                    break;
                }

                case AnalysisAction.Create:
                {
                    var newSkill = result.NewSkill!;
                    var execResult2 = await _executor.ExecuteAsync(newSkill, text);
                    if (execResult2.Success)
                    {
                        _library.Add(newSkill);
                        AddMessage("assistant",
                            $"✨ 已创建新技能「{newSkill.Name}」并执行。\n\n{result.Reply}\n\n{execResult2.Message}",
                            newSkill.Name);
                    }
                    else
                    {
                        AddMessage("assistant",
                            $"❌ 创建技能「{newSkill.Name}」失败。\n\n{result.Reply}\n\n{execResult2.Message}\n\n提示：请确认程序已安装或检查路径是否正确。");
                    }
                    break;
                }

                case AnalysisAction.Chat:
                    AddMessage("assistant", result.Reply);
                    break;
            }
        }
        catch (Exception ex)
        {
            RemoveLastTyping();
            AddMessage("assistant", $"❌ 出错了: {ex.Message}");
        }

        _sending = false;
        _sendBtn.IsEnabled = true;
    }

    private void RemoveLastMessage()
    {
        if (MessageStack.Children.Count > 0)
            MessageStack.Children.RemoveAt(MessageStack.Children.Count - 1);
    }

    private void RemoveLastTyping()
    {
        if (MessageStack.Children.Count > 0)
        {
            var last = MessageStack.Children[^1] as StackPanel;
            if (last?.Children.Count > 0 && last.Children[^1] is Border b && b.Child is TextBlock tb && tb.Text == "⏳ 思考中...")
                MessageStack.Children.RemoveAt(MessageStack.Children.Count - 1);
        }
    }

    private void ScrollToBottom()
    {
        _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            try { MessageScroll.ChangeView(null, MessageScroll.ScrollableHeight, null); }
            catch { }
        });
    }

    #region DWM

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, uint attr, ref int attrValue, int attrSize);

    private const uint DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    #endregion
}
