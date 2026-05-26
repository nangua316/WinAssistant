using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using WinAssistant.Helpers;
using WinAssistant.Models;
using WinAssistant.ViewModels;

namespace WinAssistant;

public sealed partial class MainPage : Page
{
    public MainPageViewModel ViewModel { get; }

    public MainPage()
    {
        InitializeComponent();
        ViewModel = App.GetService<MainPageViewModel>();
    }

    private ListViewDragReorder? _reorder;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.LoadSettings();
        _reorder = new ListViewDragReorder(BindingListView, ViewModel.Bindings, ViewModel.SaveSettings);
        App.HotKeyService.HotKeyPressed += OnHotKeyPressed;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        App.HotKeyService.HotKeyPressed -= OnHotKeyPressed;
    }

    private void OnHotKeyPressed(object? sender, HotKeyBinding binding)
    {
        App.DispatcherQueue.TryEnqueue(() => ViewModel.HandleHotKeyPressed(binding));
    }

    private void OnMenuSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Null guard: this fires during InitializeComponent() before x:Name fields are wired
        if (GeneralPanel == null) return;
        var index = MenuListView.SelectedIndex;
        GeneralPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        LaunchpadPanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        HotkeyPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        AddAppButton.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;

        TitleText.Text = index switch
        {
            0 => "常规设置",
            1 => "启动台设置",
            2 => "全局快捷键管理",
            _ => ""
        };
        SubtitleText.Text = index switch
        {
            0 => "设置应用程序的基本选项",
            1 => "配置启动台的触发方式和行为",
            2 => "添加应用并设置全局快捷键",
            _ => ""
        };
    }

    private void OnOpenLaunchpadClick(object sender, RoutedEventArgs e)
    {
        App.LaunchpadWindow.Open();
    }

    #region Hotkey list event handlers

    private bool _toggling;

    private async void OnToggleToggled(object sender, RoutedEventArgs e)
    {
        if (_toggling) return;
        if (sender is not ToggleSwitch toggle) return;
        if (toggle.Tag is not HotKeyBindingViewModel vm) return;

        _toggling = true;

        if (toggle.IsOn && vm.Model.Modifiers != 0 && vm.Model.VirtualKey != 0)
        {
            var conflict = ViewModel.FindBindingConflict(vm.Model.Modifiers, vm.Model.VirtualKey, vm);
            if (conflict != null)
            {
                toggle.IsOn = false;
                _toggling = false;
                _ = new ContentDialog
                {
                    Title = "快捷键冲突",
                    Content = $"无法启用：快捷键 {vm.Model.HotKeyDisplay} 已被 \"{conflict.Name}\" 使用",
                    CloseButtonText = "确定",
                    XamlRoot = this.XamlRoot
                }.ShowAsync();
                return;
            }
        }

        ViewModel.ToggleBindingCommand.Execute(vm);
        toggle.IsOn = vm.IsEnabled;
        _toggling = false;
    }

    private async void OnIconImageLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Image image) return;
        if (image.Tag is not HotKeyBindingViewModel vm) return;
        if (vm.IconSource != null) return;

        var tempFile = await Task.Run(() =>
            IconHelper.ExtractAppIconToAppData(vm.AppPath, aumid: vm.Model.Aumid));
        if (tempFile == null) return;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.UriSource = new Uri(tempFile);
            vm.IconSource = bitmap;
        }
        catch { }
    }

    #endregion
}
