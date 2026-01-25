namespace MarkupGlass.Models;

public sealed class ShapeData
{
    public ShapeType Type { get; set; }
    public double StartX { get; set; }
    public double StartY { get; set; }
    public double EndX { get; set; }
    public double EndY { get; set; }
    public string StrokeColor { get; set; } = "#FFFFFFFF";
    public double Thickness { get; set; } = 2;
}
