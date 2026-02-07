using System;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;
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

    public AppSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return new AppSettings();
        }

        var json = File.ReadAllText(_settingsPath);
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(json);
            if (settings != null)
            {
                settings.Hotkeys ??= new HotkeySettings();
                settings.ColorLibrary ??= new List<string>();
                settings.ToolbarColors ??= new List<string>();
                return settings;
            }
        }
        catch (JsonException)
        {
        }

        try
        {
            var hotkeys = JsonSerializer.Deserialize<HotkeySettings>(json);
            if (hotkeys != null)
            {
                return new AppSettings { Hotkeys = hotkeys };
            }
        }
        catch (JsonException)
        {
        }

        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true
        };
        var json = JsonSerializer.Serialize(settings, options);
        File.WriteAllText(_settingsPath, json);
    }
}
