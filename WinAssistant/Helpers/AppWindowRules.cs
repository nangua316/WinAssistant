namespace WinAssistant.Helpers;

static class AppWindowRules
{
    /// <returns>Score adjustment to add, or null if no special rule applies.</returns>
    public static int? GetScoreAdjustment(string appPath, string className, bool hasTitle)
    {
        // All Chromium-based browsers (Chrome, Edge, Brave, FoxMail etc.) and
        // WebView2 hosts use Chrome_WidgetWin_0/1 for their window class.
        // ScoreWindow penalises this class by -20 to filter out helper windows.
        // If the window has a real title, it's the main browser window —
        // neutralise the penalty.
        if (className.Contains("Chrome_WidgetWin") && hasTitle)
            return 20;
        return null;
    }
}
