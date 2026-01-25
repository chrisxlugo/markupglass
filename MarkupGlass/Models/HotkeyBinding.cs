using System.Windows.Input;

namespace MarkupGlass.Models;

public sealed class HotkeyBinding
{
    public uint Modifiers { get; set; }
    public Key Key { get; set; }
}
