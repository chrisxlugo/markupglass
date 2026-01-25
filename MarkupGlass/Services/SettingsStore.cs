using System;
using System.IO;
using System.Text.Json;
using MarkupGlass.Models;

namespace MarkupGlass.Services;

internal sealed class SettingsStore
{
    private readonly string _settingsPath;

    public SettingsStore()
    {
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MarkupGlass");
        Directory.CreateDirectory(baseDir);
        _settingsPath = Path.Combine(baseDir, "settings.json");
    }

    public HotkeySettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return new HotkeySettings();
        }

        var json = File.ReadAllText(_settingsPath);
        return JsonSerializer.Deserialize<HotkeySettings>(json) ?? new HotkeySettings();
    }

    public void Save(HotkeySettings settings)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true
        };
        var json = JsonSerializer.Serialize(settings, options);
        File.WriteAllText(_settingsPath, json);
    }
}
