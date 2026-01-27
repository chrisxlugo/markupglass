using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using UserControl = System.Windows.Controls.UserControl;
using MarkupGlass.Models;

namespace MarkupGlass.Controls;

public partial class ShapeAnnotationControl : UserControl
{
    private static readonly double DragThresholdX = SystemParameters.MinimumHorizontalDragDistance;
    private static readonly double DragThresholdY = SystemParameters.MinimumVerticalDragDistance;
    private bool _isDragging;
    private bool _isPointerDown;
    private Point _dragStart;
    private Point _dragOrigin;
    private Point _startAbsolute;
    private Point _endAbsolute;

    public event EventHandler? DragCompleted;
    public event EventHandler? ResizeCompleted;
    public event EventHandler? Selected;

    public ShapeAnnotationControl()
    {
        InitializeComponent();
        HitTarget.MouseLeftButtonDown += OnMouseDown;
        HitTarget.MouseMove += OnMouseMove;
        HitTarget.MouseLeftButtonUp += OnMouseUp;
        HitTarget.LostMouseCapture += (_, _) => ResetDragState();
        ResizeThumb.DragDelta += OnResizeDelta;
        ResizeThumb.DragCompleted += (_, _) => ResizeCompleted?.Invoke(this, EventArgs.Empty);
    }

    public ShapeType ShapeType { get; set; } = ShapeType.Rectangle;

    public Brush Stroke
    {
        get => ShapePath.Stroke;
        set => ShapePath.Stroke = value;
    }

    public double StrokeThickness
    {
        get => ShapePath.StrokeThickness;
        set => ShapePath.StrokeThickness = value;
    }

    public Point StartAbsolute => _startAbsolute;
    public Point EndAbsolute => _endAbsolute;

    public void SetSelected(bool selected)
    {
        ResizeThumb.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetAbsolutePoints(Point start, Point end)
    {
        _startAbsolute = start;
        _endAbsolute = end;
        UpdateBounds();
    }

    private void UpdateBounds()
    {
        var left = Math.Min(_startAbsolute.X, _endAbsolute.X);
        var top = Math.Min(_startAbsolute.Y, _endAbsolute.Y);
        var right = Math.Max(_startAbsolute.X, _endAbsolute.X);
        var bottom = Math.Max(_startAbsolute.Y, _endAbsolute.Y);

        var width = Math.Max(6, right - left);
        var height = Math.Max(6, bottom - top);

        Canvas.SetLeft(this, left);
        Canvas.SetTop(this, top);
        Width = width;
        Height = height;

        var localStart = new Point(_startAbsolute.X - left, _startAbsolute.Y - top);
        var localEnd = new Point(_endAbsolute.X - left, _endAbsolute.Y - top);
        Canvas.SetLeft(ResizeThumb, localEnd.X - (ResizeThumb.Width / 2));
        Canvas.SetTop(ResizeThumb, localEnd.Y - (ResizeThumb.Height / 2));
        UpdateGeometry(localStart, localEnd, width, height);
    }

    private void UpdateGeometry(Point start, Point end, double width, double height)
    {
        ShapePath.Fill = Brushes.Transparent;

        switch (ShapeType)
        {
            case ShapeType.Line:
                ShapePath.Data = new LineGeometry(start, end);
                break;
            case ShapeType.Arrow:
                ShapePath.Data = BuildArrowGeometry(start, end);
                break;
            case ShapeType.Ellipse:
                ShapePath.Data = new EllipseGeometry(new Rect(0, 0, width, height));
                break;
            case ShapeType.Rectangle:
            default:
                ShapePath.Data = new RectangleGeometry(new Rect(0, 0, width, height), 2, 2);
                break;
        }
    }

    private Geometry BuildArrowGeometry(Point start, Point end)
    {
        var vector = end - start;
        var length = vector.Length;
        if (length < 0.5)
        {
            return new LineGeometry(start, end);
        }

        vector.Normalize();

        var headLength = Math.Min(24, Math.Max(8, length * 0.2));
        var headWidth = Math.Min(16, Math.Max(6, length * 0.12));

        var basePoint = start + vector * headLength;
        var normal = new Vector(-vector.Y, vector.X);
        var left = basePoint + normal * (headWidth / 2);
        var right = basePoint - normal * (headWidth / 2);

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(basePoint, false, false);
            context.LineTo(end, true, true);
            context.BeginFigure(left, false, false);
            context.LineTo(start, true, true);
            context.LineTo(right, true, true);
        }

        geometry.Freeze();
        return geometry;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Thumb)
        {
            return;
        }

        Selected?.Invoke(this, EventArgs.Empty);
        _isPointerDown = true;
        _isDragging = false;
        _dragStart = e.GetPosition(Parent as UIElement);
        _dragOrigin = new Point(Canvas.GetLeft(this), Canvas.GetTop(this));
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPointerDown)
        {
            return;
        }

        var current = e.GetPosition(Parent as UIElement);
        if (!_isDragging)
        {
            var initialDelta = current - _dragStart;
            if (Math.Abs(initialDelta.X) < DragThresholdX && Math.Abs(initialDelta.Y) < DragThresholdY)
            {
                return;
            }

            _isDragging = true;
        }
        var offset = current - _dragStart;
        var delta = new Vector(offset.X, offset.Y);

        _startAbsolute += delta;
        _endAbsolute += delta;
        _dragStart = current;
        UpdateBounds();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPointerDown)
        {
            return;
        }

        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        var wasDragging = _isDragging;
        if (wasDragging)
        {
            DragCompleted?.Invoke(this, EventArgs.Empty);
        }

        ResetDragState();
        e.Handled = wasDragging;
    }

    private void ResetDragState()
    {
        _isDragging = false;
        _isPointerDown = false;
    }

    private void OnResizeDelta(object sender, DragDeltaEventArgs e)
    {
        if (Parent is not UIElement parent)
        {
            return;
        }

        var current = Mouse.GetPosition(parent);
        _endAbsolute = current;
        UpdateBounds();
    }
}
