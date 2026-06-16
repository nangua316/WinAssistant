using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media;
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
    public string IconPath { get; } = string.Empty;
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
                OnPropertyChanged(nameof(IsInteractive));
            }
        }
    }

    /// <summary>Display text for the add button.</summary>
    public string AddButtonText => IsAdded ? "已添加" : "添加";

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

    public AppPickerItem(string name, string appPath, string arguments = "", string aumid = "", int usageCount = 0, Action<AppPickerItem>? addAction = null, string shortcutPath = "", string? iconPath = null)
    {
        Name = name;
        AppPath = appPath;
        Arguments = arguments;
        Aumid = aumid;
        ShortcutPath = shortcutPath;
        IconPath = iconPath ?? string.Empty;
        UsageCount = usageCount;
        IconDisplayChar = name.Length > 0 ? name[..1].ToUpper() : "?";
        _addAction = addAction;
        PinyinSearchData = ComputePinyinSearchData(name);
    }

    private static string ComputePinyinSearchData(string name) => PinyinHelper.GetSearchData(name);
}
