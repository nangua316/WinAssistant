using System.Diagnostics;
using System.Text.Json;
using WinAssistant.Models;

namespace WinAssistant.Services;

public class SettingsService
{
    private readonly string _filePath;
    private readonly string _tempPath;
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public SettingsService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(appData, "WinAssistant");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "settings.json");
        _tempPath = _filePath + ".tmp";
    }

    /// <summary>
    /// Load settings, supporting both old (JSON array) and new (wrapped object) formats.
    /// </summary>
    public AppSettings Load()
    {
        if (!File.Exists(_filePath))
            return new AppSettings();

        try
        {
            var json = File.ReadAllText(_filePath);

            // Old format: JSON array of HotKeyBinding — wrap into AppSettings
            if (json.TrimStart().StartsWith('['))
            {
                var bindings = JsonSerializer.Deserialize<List<HotKeyBinding>>(json) ?? [];
                return new AppSettings { Bindings = bindings };
            }

            // New format: AppSettings object
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsService] Load failed: {ex.Message}");
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            lock (_lock)
            {
                // Atomic write: temp file → rename, so a crash mid-write never corrupts
                // the real settings file.
                File.WriteAllText(_tempPath, json);
                File.Move(_tempPath, _filePath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsService] Save failed: {ex.Message}");
        }
    }
}
