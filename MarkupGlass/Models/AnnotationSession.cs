using System.Collections.Generic;

namespace MarkupGlass.Models;

public sealed class AnnotationSession
{
    public List<StrokeData> Strokes { get; set; } = new();
    public List<TextBoxData> TextBoxes { get; set; } = new();
    public List<ShapeData> Shapes { get; set; } = new();
}
