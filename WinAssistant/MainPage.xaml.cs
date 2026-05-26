using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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
        // Must init after LoadSettings, which creates the Bindings collection
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

    private bool _toggling;

    private async void OnToggleToggled(object sender, RoutedEventArgs e)
    {
        if (_toggling) return;
        if (sender is not ToggleSwitch toggle) return;
        if (toggle.Tag is not HotKeyBindingViewModel vm) return;

        _toggling = true;

        // If trying to enable, check conflict BEFORE toggling
        if (toggle.IsOn && vm.Model.Modifiers != 0 && vm.Model.VirtualKey != 0)
        {
            var conflict = ViewModel.FindBindingConflict(vm.Model.Modifiers, vm.Model.VirtualKey, vm);
            if (conflict != null)
            {
                toggle.IsOn = false; // prevented — guard catches re-entrant Toggled
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

        // Let ViewModel handle the toggle
        ViewModel.ToggleBindingCommand.Execute(vm);

        // Sync switch with ViewModel's final state
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

    private async void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Spacing = 12, MinWidth = 350 };

        // Auto-start
        var autoStartToggle = new ToggleSwitch
        {
            IsOn = ViewModel.IsAutoStart,
            OnContent = "开机自启动",
            OffContent = "开机自启动"
        };
        autoStartToggle.Toggled += (_, _) => ViewModel.IsAutoStart = autoStartToggle.IsOn;
        panel.Children.Add(autoStartToggle);

        // Separator
        panel.Children.Add(new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Colors.Gray),
            Opacity = 0.15,
            Margin = new Thickness(0, 4, 0, 4)
        });

        // Launchpad section header
        panel.Children.Add(new TextBlock
        {
            Text = "Launchpad 启动台",
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"]
        });

        // Launchpad enable toggle
        var lpToggle = new ToggleSwitch
        {
            IsOn = ViewModel.IsLaunchpadEnabled,
            OnContent = "启用 Launchpad",
            OffContent = "启用 Launchpad"
        };
        panel.Children.Add(lpToggle);

        // Trigger mode row
        var triggerRow = new Grid
        {
            Margin = new Thickness(0, 0, 0, 4),
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            }
        };

        var triggerLabel = new TextBlock
        {
            Text = "触发方式",
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(triggerLabel, 0);
        triggerRow.Children.Add(triggerLabel);

        var triggerCombo = new ComboBox
        {
            SelectedValuePath = "Tag",
            Width = 200,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        triggerCombo.Items.Add(new ComboBoxItem { Tag = "DoubleCtrl", Content = "双击 Ctrl" });
        triggerCombo.Items.Add(new ComboBoxItem { Tag = "DoubleAlt", Content = "双击 Alt" });
        triggerCombo.Items.Add(new ComboBoxItem { Tag = "DoubleShift", Content = "双击 Shift" });
        triggerCombo.Items.Add(new ComboBoxItem { Tag = "DoubleWin", Content = "双击 Win" });
        triggerCombo.Items.Add(new ComboBoxItem { Tag = "SingleWin", Content = "单按 Win" });
        triggerCombo.SelectedValue = ViewModel.LaunchpadTrigger;
        triggerCombo.SelectionChanged += (_, _) =>
        {
            if (triggerCombo.SelectedValue is string tag)
                ViewModel.LaunchpadTrigger = tag;
        };
        Grid.SetColumn(triggerCombo, 1);
        triggerRow.Children.Add(triggerCombo);

        panel.Children.Add(triggerRow);

        // Open Launchpad button
        var openButton = new Button
        {
            Content = "打开 Launchpad",
            Padding = new Thickness(16, 8, 16, 8),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Left
        };

        ContentDialog settingsDialog = null!;
        openButton.Click += (_, _) =>
        {
            settingsDialog.Hide();
            App.LaunchpadWindow.Open();
        };
        panel.Children.Add(openButton);

        // Toggle trigger visibility with launchpad enable state
        void UpdateTriggerVisibility() =>
            triggerRow.Visibility = lpToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        UpdateTriggerVisibility();
        lpToggle.Toggled += (_, _) => { ViewModel.IsLaunchpadEnabled = lpToggle.IsOn; UpdateTriggerVisibility(); };

        settingsDialog = new ContentDialog
        {
            Title = "设置",
            Content = panel,
            CloseButtonText = "关闭",
            XamlRoot = this.XamlRoot
        };
        await settingsDialog.ShowAsync();
    }
}
