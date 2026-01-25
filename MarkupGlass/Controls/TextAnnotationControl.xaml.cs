using System;
using Point = System.Windows.Point;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using Key = System.Windows.Input.Key;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using UIElement = System.Windows.UIElement;
using UserControl = System.Windows.Controls.UserControl;
using Canvas = System.Windows.Controls.Canvas;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using DragDeltaEventArgs = System.Windows.Controls.Primitives.DragDeltaEventArgs;
using Visibility = System.Windows.Visibility;

namespace MarkupGlass.Controls;

public partial class TextAnnotationControl : UserControl
{
    private bool _isDragging;
    private Point _dragStart;
    private Point _dragOrigin;
    private bool _isSelected;

    public event EventHandler? DragCompleted;
    public event EventHandler? ResizeCompleted;
    public event EventHandler? EditCompleted;
    public event EventHandler? Selected;

    public TextAnnotationControl()
    {
        InitializeComponent();
        Editor.PreviewMouseLeftButtonDown += (_, _) => Selected?.Invoke(this, EventArgs.Empty);
        RootBorder.MouseLeftButtonDown += OnMouseDown;
        RootBorder.MouseMove += OnMouseMove;
        RootBorder.MouseLeftButtonUp += OnMouseUp;
        RootBorder.MouseLeftButtonDown += OnMouseLeftButtonDown;
        ResizeThumb.DragDelta += OnResizeDelta;
        ResizeThumb.DragCompleted += (_, _) => ResizeCompleted?.Invoke(this, EventArgs.Empty);
        Editor.LostKeyboardFocus += (_, _) => EndEdit();
        Editor.PreviewKeyDown += OnEditorKeyDown;
    }

    public string Text
    {
        get => Editor.Text;
        set => Editor.Text = value;
    }

    public bool HasBackground
    {
        get => RootBorder.Background != Brushes.Transparent;
        set => ApplyBackground(value);
    }

    public Brush TextBrush
    {
        get => Editor.Foreground;
        set
        {
            Editor.Foreground = value;
            ApplyBorderBrush();
            ApplyBackground(HasBackground);
        }
    }

    public double FontSizeValue
    {
        get => Editor.FontSize;
        set => Editor.FontSize = value;
    }

    public void BeginEdit()
    {
        Editor.IsReadOnly = false;
        Editor.Focus();
        Editor.CaretIndex = Editor.Text.Length;
    }

    public void EndEdit()
    {
        if (Editor.IsReadOnly)
        {
            return;
        }

        Editor.IsReadOnly = true;
        EditCompleted?.Invoke(this, EventArgs.Empty);
    }

    public void SetSelected(bool selected)
    {
        _isSelected = selected;
        ResizeThumb.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        ApplyBorderBrush();
    }

    private void ApplyBackground(bool enabled)
    {
        if (!enabled)
        {
            RootBorder.Background = Brushes.Transparent;
            return;
        }

        RootBorder.Background = new SolidColorBrush(Color.FromArgb(153, 0, 0, 0));
    }

    private void ApplyBorderBrush()
    {
        var color = Colors.White;
        if (Editor.Foreground is SolidColorBrush brush)
        {
            color = brush.Color;
        }

        var alpha = _isSelected ? (byte)255 : (byte)102;
        RootBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!Editor.IsReadOnly)
        {
            return;
        }

        Selected?.Invoke(this, EventArgs.Empty);
        _isDragging = true;
        _dragStart = e.GetPosition(Parent as UIElement);
        _dragOrigin = new Point(Canvas.GetLeft(this), Canvas.GetTop(this));
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            BeginEdit();
            e.Handled = true;
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        var current = e.GetPosition(Parent as UIElement);
        var offset = current - _dragStart;
        Canvas.SetLeft(this, _dragOrigin.X + offset.X);
        Canvas.SetTop(this, _dragOrigin.Y + offset.Y);
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        ReleaseMouseCapture();
        DragCompleted?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }


    private void OnResizeDelta(object sender, DragDeltaEventArgs e)
    {
        var newWidth = Math.Max(50, ActualWidth + e.HorizontalChange);
        var newHeight = Math.Max(30, ActualHeight + e.VerticalChange);
        Width = newWidth;
        Height = newHeight;
    }

    private void OnEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            EndEdit();
            e.Handled = true;
        }
    }
}
