namespace WinAssistant;

/// <summary>
/// Hosts the Launchpad UI inside the application's single <see cref="MainWindow"/>.
/// This avoids creating a second <see cref="Microsoft.UI.Xaml.Window"/>, which
/// crashes published unpackaged WinUI 3 apps (CoreMessagingXP.dll, 0xc0000602).
/// </summary>
public sealed class LaunchpadHost
{
    public bool IsShowing => (App.Window as MainWindow)?.IsLaunchpadShowing ?? false;

    public void Open() => (App.Window as MainWindow)?.ShowLaunchpad();

    public void Close() => (App.Window as MainWindow)?.CloseLaunchpad();
}
