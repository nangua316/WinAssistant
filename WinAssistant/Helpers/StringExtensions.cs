namespace WinAssistant.Helpers;

internal static class StringExtensions
{
    /// <summary>
    /// Truncates the string to the specified maximum length, appending "…" if truncated.
    /// </summary>
    public static string Truncate(this string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (maxLength <= 0) return "";
        if (value.Length <= maxLength) return value;
        return value[..(maxLength - 1)] + "…";
    }
}
