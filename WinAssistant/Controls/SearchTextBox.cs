using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WinAssistant.Controls;

/// <summary>
/// A TextBox variant that removes the built-in DeleteButton (the "X"
/// clear button) from its template. Used in the Launchpad search bar
/// so that the clear button does not overlap with the right-aligned pin toggle.
/// </summary>
public class SearchTextBox : TextBox
{
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        // Remove the built-in DeleteButton from the template's visual tree.
        // GetTemplateChild is protected — we access it from this subclass.
        if (GetTemplateChild("DeleteButton") is Button deleteButton)
        {
            var rootGrid = VisualTreeHelper.GetChild(this, 0) as Grid;
            rootGrid?.Children.Remove(deleteButton);
        }
    }
}
