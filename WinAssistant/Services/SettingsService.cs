using System.Text.Json;
using WinAssistant.Models;

namespace WinAssistant.Services;

public class SettingsService
{
    private readonly string _filePath;
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
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(_filePath, json);
    }
}
