namespace WinAssistant.Helpers;

static class AppWindowRules
{
    /// <returns>Score adjustment to add, or null if no special rule applies.</returns>
    public static int? GetScoreAdjustment(string appPath, string className, bool hasTitle)
    {
        // Determine the exe name (without extension) from the full app path.
        var exeName = Path.GetFileNameWithoutExtension(appPath.AsSpan());
        if (exeName.Length == 0) return null;

        return exeName.Equals("foxmail", StringComparison.OrdinalIgnoreCase)
            ? FoxMailAdjustment(className, hasTitle)
            : null;
    }

    private static int? FoxMailAdjustment(string className, bool hasTitle)
    {
        // FoxMail is Electron-based; its main window uses Chrome_WidgetWin_0.
        // The generic ScoreWindow penalises Chrome_WidgetWin by -20, which
        // causes the main window to lose to an empty-title helper.
        // If this window has a real title, neutralise the penalty.
        if (className.Contains("Chrome_WidgetWin") && hasTitle)
            return 20;
        return null;
    }
}
