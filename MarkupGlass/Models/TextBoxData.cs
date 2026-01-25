namespace MarkupGlass.Models;

public sealed class TextBoxData
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 200;
    public double Height { get; set; } = 60;
    public string Text { get; set; } = string.Empty;
    public double FontSize { get; set; } = 16;
    public string TextColor { get; set; } = "#FFFFFFFF";
    public bool HasBackground { get; set; }
}
