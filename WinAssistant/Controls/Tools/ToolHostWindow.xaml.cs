using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
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
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;

        var (w, h) = tool.DefaultWindowSize;
        AppWindow.Resize(new SizeInt32((int)w, (int)h));

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
        win.Activate();
    }

    public static void CloseAll()
    {
        foreach (var w in _instances.Values.ToList())
            w.Close();
        _instances.Clear();
    }
}
