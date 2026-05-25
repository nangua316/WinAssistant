namespace WinAssistant.Helpers;

public static class SearchHelper
{
    public static bool FuzzyMatch(string text, string query)
    {
        if (string.IsNullOrEmpty(query)) return true;
        if (string.IsNullOrEmpty(text)) return false;

        if (text.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;

        int ti = 0;
        foreach (var qc in query)
        {
            while (ti < text.Length &&
                   char.ToLowerInvariant(text[ti]) != char.ToLowerInvariant(qc))
                ti++;
            if (ti >= text.Length) return false;
            ti++;
        }
        return true;
    }

    /// <summary>
    /// Match query against pinyin search data (space-separated segments like "wx weixin").
    /// Each segment is matched independently to avoid cross-segment false positives.
    /// </summary>
    public static bool FuzzyMatchPinyin(string pinyinData, string query)
    {
        if (string.IsNullOrEmpty(query)) return true;
        if (string.IsNullOrEmpty(pinyinData)) return false;

        foreach (var segment in pinyinData.Split(' '))
        {
            if (string.IsNullOrEmpty(segment)) continue;
            if (segment.Contains(query, StringComparison.OrdinalIgnoreCase))
                return true;
            int si = 0;
            foreach (var qc in query)
            {
                while (si < segment.Length &&
                       char.ToLowerInvariant(segment[si]) != char.ToLowerInvariant(qc))
                    si++;
                if (si >= segment.Length) goto nextSegment;
                si++;
            }
            return true;
            nextSegment:;
        }
        return false;
    }
}
