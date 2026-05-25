using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WinAssistant.Helpers;

namespace WinAssistant.Models;

/// <summary>
/// Raw data from app scanning (non-observable, used for initial scan).
/// </summary>
public class InstalledAppInfo
{
    public string Name { get; set; } = string.Empty;
    public string AppPath { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string ShortcutPath { get; set; } = string.Empty;
    public string IconPath { get; set; } = string.Empty;
    public string IconDisplayChar { get; set; } = "";
    public string Aumid { get; set; } = "";
    public int UsageCount { get; set; }
}

/// <summary>
/// Observable wrapper for display in the app picker dialog.
/// </summary>
public class AppPickerItem : ObservableObject
{
    private ImageSource? _iconSource;
    private bool _isAdded;
    private RelayCommand? _addCommand;

    public string Name { get; }
    public string AppPath { get; }
    public string Arguments { get; }
    public string ShortcutPath { get; }
    public string IconDisplayChar { get; }
    public int UsageCount { get; }
    public string Aumid { get; }

    /// <summary>
    /// Whether this app has been added to the main page.
    /// </summary>
    public bool IsAdded
    {
        get => _isAdded;
        set
        {
            if (SetProperty(ref _isAdded, value))
            {
                OnPropertyChanged(nameof(AddButtonText));
                OnPropertyChanged(nameof(AddButtonBackground));
                OnPropertyChanged(nameof(AddButtonForeground));
                OnPropertyChanged(nameof(IsInteractive));
            }
        }
    }

    /// <summary>Display text for the add button.</summary>
    public string AddButtonText => IsAdded ? "已添加" : "添加";

    /// <summary>Button background color: green for added, accent blue for add.</summary>
    public Brush AddButtonBackground => IsAdded
        ? new SolidColorBrush(Color.FromArgb(255, 80, 80, 80))
        : new SolidColorBrush(Color.FromArgb(255, 0, 103, 192));

    /// <summary>Button text color: white for both states.</summary>
    public Brush AddButtonForeground => new SolidColorBrush(Colors.White);

    /// <summary>Whether the add button is interactive (enabled). False when already added.</summary>
    public bool IsInteractive => !_isAdded;

    /// <summary>Command to add this app.</summary>
    public ICommand AddCommand => _addCommand ??= new RelayCommand(ExecuteAdd);

    private Action<AppPickerItem>? _addAction;

    private void ExecuteAdd()
    {
        if (_isAdded) return;
        _addAction?.Invoke(this);
        IsAdded = true;
    }

    /// <summary>
    /// Set or replace the add action. Used when the action isn't available at construction time.
    /// </summary>
    public void SetAddAction(Action<AppPickerItem> action) => _addAction = action;

    public ImageSource? IconSource
    {
        get => _iconSource;
        set => SetProperty(ref _iconSource, value);
    }

    internal string PinyinSearchData { get; }

    public AppPickerItem(string name, string appPath, string arguments = "", string aumid = "", int usageCount = 0, Action<AppPickerItem>? addAction = null, string shortcutPath = "")
    {
        Name = name;
        AppPath = appPath;
        Arguments = arguments;
        Aumid = aumid;
        ShortcutPath = shortcutPath;
        UsageCount = usageCount;
        IconDisplayChar = name.Length > 0 ? name[..1].ToUpper() : "?";
        _addAction = addAction;
        PinyinSearchData = ComputePinyinSearchData(name);
    }

    private static string ComputePinyinSearchData(string name)
    {
        var initials = PinyinHelper.GetInitials(name);
        var full = PinyinHelper.GetPinyin(name);
        if (string.IsNullOrEmpty(initials)) return full;
        if (string.Equals(initials, full, StringComparison.OrdinalIgnoreCase))
            return full;
        return $"{initials} {full}";
    }
}
