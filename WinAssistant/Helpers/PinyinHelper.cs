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

    /// <summary>
    /// 计算拼音搜索数据（首字母 + 空格 + 全拼），用于模糊搜索。
    /// 三个调用点统一入口，避免重复代码。
    /// </summary>
    public static string GetSearchData(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var initials = GetInitials(name);
        var full = GetPinyin(name);
        if (string.IsNullOrEmpty(initials)) return full;
        if (string.Equals(initials, full, StringComparison.OrdinalIgnoreCase))
            return full;
        return $"{initials} {full}";
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
