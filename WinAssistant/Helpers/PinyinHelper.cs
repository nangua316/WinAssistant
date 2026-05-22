using System.Text;
using ToolGood.Words;

namespace WinAssistant.Helpers;

public static class PinyinHelper
{
    /// <summary>Get pinyin initials for Chinese text. "微信" → "wx"</summary>
    public static string GetInitials(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return WordsHelper.GetFirstPinyin(text).ToLowerInvariant();
    }

    /// <summary>Get full pinyin (without tone marks). "微信" → "wei xin"</summary>
    public static string GetPinyin(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var raw = WordsHelper.GetPinyin(text);
        return StripToneMarks(raw).ToLowerInvariant();
    }

    private static string StripToneMarks(string pinyin)
    {
        if (string.IsNullOrEmpty(pinyin)) return pinyin;
        var sb = new StringBuilder(pinyin.Length);
        foreach (var c in pinyin)
        {
            if (c > 0x7e)
            {
                var normalized = c.ToString().Normalize(NormalizationForm.FormD);
                sb.Append(normalized.Length > 0 ? normalized[0] : c);
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
