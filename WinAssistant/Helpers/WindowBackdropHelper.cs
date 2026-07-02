using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WinAssistant.Helpers;

/// <summary>
/// Chooses the appropriate system backdrop for the current OS version.
/// Mica is only supported on Windows 11 (build 22000+).
/// On Windows 10 we fall back to a solid theme background to avoid a blank/transparent window.
/// </summary>
internal static class WindowBackdropHelper
{
    public static bool IsMicaSupported => Environment.OSVersion.Version.Build >= 22000;

    public static bool ApplyMicaOrSolidBackground(Window window, FrameworkElement root)
    {
        if (IsMicaSupported)
        {
            window.SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
            return true;
        }

        window.SystemBackdrop = null;
        if (root is Panel panel)
        {
            panel.Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
        }
        else if (root is Border border)
        {
            border.Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
        }
        return false;
    }
}
