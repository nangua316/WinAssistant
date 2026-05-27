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
    /// Optional file path to extract the tool icon from (e.g. WeChat.exe).
    /// When set, the launchpad loads and displays the extracted icon instead of the glyph.
    /// </summary>
    string? IconExtractPath => null;

    /// <summary>
    /// Called when the tool is clicked in the launchpad
    /// (only used when <see cref="IsOneClickAction"/> is true).
    /// Returns a brief toast message, or null for no toast.
    /// </summary>
    string? Activate() => null;

    /// <summary>
    /// If the tool needs to show a ContentDialog after Activate(), create it here.
    /// The dialog's XamlRoot is set by the launchpad before showing.
    /// Returns null (default) for tools that don't need a dialog.
    /// </summary>
    ContentDialog? CreateActivateDialog() => null;

    /// <summary>
    /// Called after the dialog from <see cref="CreateActivateDialog"/> is closed.
    /// Override this to handle dialog results (e.g. copy to clipboard).
    /// </summary>
    void OnActivateDialogResult(ContentDialog dialog, ContentDialogResult result) { }
}
