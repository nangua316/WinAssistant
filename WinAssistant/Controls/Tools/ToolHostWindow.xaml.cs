using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace WinAssistant.Controls.Tools;

public sealed partial class ToolHostWindow : Window
{
    private static readonly Dictionary<string, ToolHostWindow> _instances = [];
    private readonly IAssistantTool _tool;

    private ToolHostWindow(IAssistantTool tool)
    {
        InitializeComponent();
        _tool = tool;

        Title = tool.Name;
        ExtendsContentIntoTitleBar = true;

        // Follow app's theme (light/dark)
        ContentRoot.RequestedTheme = App.Window.Content is FrameworkElement fe
            ? fe.RequestedTheme
            : ElementTheme.Default;

        var content = tool.CreateContent();
        ContentRoot.Children.Add(content);

        Closed += OnClosed;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _instances.Remove(_tool.Id);
    }

    public static void OpenOrActivate(IAssistantTool tool)
    {
        if (_instances.TryGetValue(tool.Id, out var win))
        {
            win.Activate();
            return;
        }

        win = new ToolHostWindow(tool);
        _instances[tool.Id] = win;

        // Show off-screen first so WinUI content renders before the user sees it
        var (w, h) = tool.DefaultWindowSize;
        win.AppWindow.MoveAndResize(new RectInt32(-9999, -9999, (int)w, (int)h));
        win.Activate();

        // After composition, move to centered position
        var timer = new Microsoft.UI.Xaml.DispatcherTimer();
        timer.Interval = TimeSpan.FromMilliseconds(60);
        timer.Tick += (s, e) =>
        {
            timer.Stop();
            CenterOnScreen(win, tool);
        };
        timer.Start();
    }

    private static void CenterOnScreen(ToolHostWindow win, IAssistantTool tool)
    {
        try
        {
            var (w, h) = tool.DefaultWindowSize;
            var display = DisplayArea.GetFromWindowId(win.AppWindow.Id, DisplayAreaFallback.Nearest);
            var workArea = display.WorkArea;
            var x = workArea.X + (workArea.Width - (int)w) / 2;
            var y = workArea.Y + (workArea.Height - (int)h) / 2;
            win.AppWindow.Move(new PointInt32(x, y));
        }
        catch { }
    }

    public static void CloseById(string toolId)
    {
        if (_instances.TryGetValue(toolId, out var win))
        {
            win.Close();
        }
    }

    public static void CloseAll()
    {
        foreach (var w in _instances.Values.ToList())
            w.Close();
        _instances.Clear();
    }
}
