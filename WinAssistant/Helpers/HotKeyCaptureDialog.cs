using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace WinAssistant.Helpers;

/// <summary>
/// Reusable hotkey capture dialog. Returns the modifier flags, virtual key and display string.
/// </summary>
internal static class HotKeyCaptureDialog
{
    public static async Task<(uint modifiers, uint virtualKey, string display, bool confirmed)> ShowAsync(
        XamlRoot xamlRoot, string title)
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
            Title = title,
            Content = inputBox,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot
        };

        inputBox.KeyDown += (s, ke) =>
        {
            var key = ke.Key;

            // Reset any previously captured combo so partial/modifier-only strokes don't
            // leave stale values behind when the user clicks OK.
            capturedMods = 0;
            capturedVk = 0;
            capturedDisplay = "";

            uint mods = 0;
            if (IsKeyDown(VirtualKey.Control)) mods |= KeyHelper.MOD_CONTROL;
            if (IsKeyDown(VirtualKey.Menu)) mods |= KeyHelper.MOD_ALT;
            if (IsKeyDown(VirtualKey.Shift)) mods |= KeyHelper.MOD_SHIFT;
            if (IsKeyDown(VirtualKey.LeftWindows) || IsKeyDown(VirtualKey.RightWindows))
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

        try
        {
            var result = await dialog.ShowAsync();
            bool confirmed = result == ContentDialogResult.Primary;
            return (capturedMods, capturedVk, capturedDisplay, confirmed);
        }
        catch
        {
            return (0, 0, "", false);
        }
    }

    private static bool IsKeyDown(VirtualKey key)
    {
        return InputKeyboardSource.GetKeyStateForCurrentThread(key)
            .HasFlag(CoreVirtualKeyStates.Down);
    }
}
