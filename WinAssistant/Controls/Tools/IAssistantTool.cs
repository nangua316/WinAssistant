using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinAssistant.Controls.Tools;

public interface IAssistantTool
{
    string Id { get; }
    string Name { get; }
    string Description { get; }
    string IconGlyph { get; }

    /// <summary>
    /// Icon foreground as a hex color string (e.g. "#FF60A5FA").
    /// Defaults to accent blue if null.
    /// </summary>
    string? IconColorHex { get; }

    UIElement CreateContent();

    UIElement? CreateSettingsContent();

    (double width, double height) DefaultWindowSize { get; }

    /// <summary>
    /// If true, clicking the tool in the launchpad calls <see cref="Activate"/>
    /// directly instead of opening a window. Default false.
    /// </summary>
    bool IsOneClickAction => false;

    /// <summary>
    /// Called when the tool is clicked in the launchpad
    /// (only used when <see cref="IsOneClickAction"/> is true).
    /// Returns a brief toast message, or null for no toast.
    /// </summary>
    string? Activate() => null;
}
