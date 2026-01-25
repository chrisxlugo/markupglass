namespace MarkupGlass.Models;

public sealed class HotkeySettings
{
    public HotkeyBinding ToggleCursor { get; set; } = new() { Modifiers = 0, Key = System.Windows.Input.Key.F8 };
    public HotkeyBinding ToggleVisibility { get; set; } = new() { Modifiers = 0, Key = System.Windows.Input.Key.F9 };
    public HotkeyBinding CloseApp { get; set; } = new() { Modifiers = 0, Key = System.Windows.Input.Key.F10 };
    public HotkeyBinding ClearAll { get; set; } = new() { Modifiers = 0x0002 | 0x0004, Key = System.Windows.Input.Key.C };
    public HotkeyBinding Undo { get; set; } = new() { Modifiers = 0x0002, Key = System.Windows.Input.Key.Z };
    public HotkeyBinding SelectPen { get; set; } = new() { Modifiers = 0, Key = System.Windows.Input.Key.None };
    public HotkeyBinding SelectHighlighter { get; set; } = new() { Modifiers = 0, Key = System.Windows.Input.Key.None };
    public HotkeyBinding SelectEraser { get; set; } = new() { Modifiers = 0, Key = System.Windows.Input.Key.None };
    public HotkeyBinding ShapeLine { get; set; } = new() { Modifiers = 0, Key = System.Windows.Input.Key.None };
    public HotkeyBinding ShapeArrow { get; set; } = new() { Modifiers = 0, Key = System.Windows.Input.Key.None };
    public HotkeyBinding ShapeRectangle { get; set; } = new() { Modifiers = 0, Key = System.Windows.Input.Key.None };
    public HotkeyBinding ShapeEllipse { get; set; } = new() { Modifiers = 0, Key = System.Windows.Input.Key.None };
    public HotkeyBinding ToggleColorPalette { get; set; } = new() { Modifiers = 0, Key = System.Windows.Input.Key.None };
}
