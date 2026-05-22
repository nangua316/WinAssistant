using System.Runtime.InteropServices;
using System.Text;

namespace WinAssistant.Helpers;

public static class KeyHelper
{
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;

    public static string GetModifierDisplay(uint modifiers)
    {
        var parts = new List<string>();
        if ((modifiers & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & MOD_WIN) != 0) parts.Add("Win");
        return string.Join(" + ", parts);
    }

    public static string GetKeyDisplay(uint virtualKey)
    {
        var lParam = (uint)MapVirtualKey(virtualKey, 0) << 16;
        var sb = new StringBuilder(256);
        GetKeyNameText((nint)lParam, sb, sb.Capacity);
        var name = sb.ToString();
        return string.IsNullOrEmpty(name) ? $"VK_{virtualKey}" : name;
    }

    public static string GetFullDisplay(uint modifiers, uint virtualKey)
    {
        var modPart = GetModifierDisplay(modifiers);
        var keyPart = GetKeyDisplay(virtualKey);
        return string.IsNullOrEmpty(modPart) ? keyPart : $"{modPart} + {keyPart}";
    }

    [DllImport("user32.dll")]
    private static extern int MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetKeyNameText(nint lParam, [Out] StringBuilder lpString, int nSize);
}
