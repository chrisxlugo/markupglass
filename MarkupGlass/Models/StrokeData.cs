using System.Collections.Generic;

namespace MarkupGlass.Models;

public sealed class StrokeData
{
    public List<PointData> Points { get; set; } = new();
    public string Color { get; set; } = "#FF000000";
    public double Thickness { get; set; } = 2;
    public double Opacity { get; set; } = 1;
    public bool IsHighlighter { get; set; }
}

public sealed class PointData
{
    public double X { get; set; }
    public double Y { get; set; }
}
