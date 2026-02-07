using System.Collections.Generic;

namespace MarkupGlass.Models;

public sealed class AppSettings
{
    public HotkeySettings Hotkeys { get; set; } = new();
    public List<string> ColorLibrary { get; set; } = new();
    public List<string> ToolbarColors { get; set; } = new();
}
