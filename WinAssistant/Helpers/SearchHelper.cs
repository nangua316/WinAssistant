namespace WinAssistant.Helpers;

public static class SearchHelper
{
    public static bool FuzzyMatch(string text, string query)
    {
        if (string.IsNullOrEmpty(query)) return true;
        if (string.IsNullOrEmpty(text)) return false;

        if (text.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;

        // Pre-lowercase for faster sequential matching
        var lowerText = text.ToLowerInvariant();
        var lowerQuery = query.ToLowerInvariant();

        int ti = 0;
        foreach (var qc in lowerQuery)
        {
            while (ti < lowerText.Length && lowerText[ti] != qc)
                ti++;
            if (ti >= lowerText.Length) return false;
            ti++;
        }
        return true;
    }

    /// <summary>
    /// Match query against pinyin search data (space-separated segments like "wx weixin").
    /// Each segment is matched independently; also tries matching against the
    /// concatenated form so that "weixin" matches "wei xin".
    /// </summary>
    public static bool FuzzyMatchPinyin(string pinyinData, string query)
    {
        if (string.IsNullOrEmpty(query)) return true;
        if (string.IsNullOrEmpty(pinyinData)) return false;

        var lowerQuery = query.ToLowerInvariant();

        // Try matching the full concatenated pinyin (e.g. "weixin" in "wx wei xin")
        var full = pinyinData.Replace(" ", "");
        if (full.Contains(lowerQuery, StringComparison.OrdinalIgnoreCase))
            return true;

        var lowerFull = full.ToLowerInvariant();
        int ti = 0;
        foreach (var qc in lowerQuery)
        {
            while (ti < lowerFull.Length && lowerFull[ti] != qc)
                ti++;
            if (ti >= lowerFull.Length) goto checkSegments;
            ti++;
        }
        return true;

        checkSegments:
        foreach (var segment in pinyinData.Split(' '))
        {
            if (string.IsNullOrEmpty(segment)) continue;
            if (segment.Contains(lowerQuery, StringComparison.OrdinalIgnoreCase))
                return true;
            var lowerSeg = segment.ToLowerInvariant();
            int si = 0;
            foreach (var qc in lowerQuery)
            {
                while (si < lowerSeg.Length && lowerSeg[si] != qc)
                    si++;
                if (si >= lowerSeg.Length) goto nextSegment;
                si++;
            }
            return true;
            nextSegment:;
        }
        return false;
    }
}
