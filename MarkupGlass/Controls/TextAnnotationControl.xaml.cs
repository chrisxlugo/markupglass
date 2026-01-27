using System;
using System.Windows;
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
    private static readonly double DragThresholdX = SystemParameters.MinimumHorizontalDragDistance;
    private static readonly double DragThresholdY = SystemParameters.MinimumVerticalDragDistance;
    private bool _isDragging;
    private bool _isPointerDown;
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
        SetEditorInteraction(false);
        Editor.PreviewMouseLeftButtonDown += (_, _) => Selected?.Invoke(this, EventArgs.Empty);
        RootBorder.MouseLeftButtonDown += OnMouseDown;
        RootBorder.MouseMove += OnMouseMove;
        RootBorder.MouseLeftButtonUp += OnMouseUp;
        RootBorder.LostMouseCapture += (_, _) => ResetDragState();
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

    public bool IsEditing => !Editor.IsReadOnly;

    public bool CanEdit { get; set; } = true;
    public bool CanDrag { get; set; } = true;

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
        if (!CanEdit)
        {
            return;
        }

        SetEditorInteraction(true);
        Editor.Focus();
        Editor.CaretIndex = Editor.Text.Length;
    }

    public void EndEdit()
    {
        if (!IsEditing)
        {
            return;
        }

        SetEditorInteraction(false);
        EditCompleted?.Invoke(this, EventArgs.Empty);
    }

    public void SetSelected(bool selected)
    {
        _isSelected = selected;
        ResizeThumb.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        ApplyBorderBrush();
    }

    private void SetEditorInteraction(bool enabled)
    {
        Editor.IsReadOnly = !enabled;
        Editor.IsHitTestVisible = enabled;
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
        if (e.OriginalSource is System.Windows.Controls.Primitives.Thumb)
        {
            return;
        }

        Selected?.Invoke(this, EventArgs.Empty);
        if (IsEditing || !CanDrag)
        {
            return;
        }

        if (e.ClickCount == 2 && CanEdit)
        {
            BeginEdit();
            e.Handled = true;
            return;
        }

        _isPointerDown = true;
        _isDragging = false;
        _dragStart = e.GetPosition(Parent as UIElement);
        var left = Canvas.GetLeft(this);
        var top = Canvas.GetTop(this);
        _dragOrigin = new Point(double.IsNaN(left) ? 0 : left, double.IsNaN(top) ? 0 : top);
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPointerDown || IsEditing)
        {
            return;
        }

        var current = e.GetPosition(Parent as UIElement);
        if (!_isDragging)
        {
            var delta = current - _dragStart;
            if (Math.Abs(delta.X) < DragThresholdX && Math.Abs(delta.Y) < DragThresholdY)
            {
                return;
            }

            _isDragging = true;
        }

        var offset = current - _dragStart;
        Canvas.SetLeft(this, _dragOrigin.X + offset.X);
        Canvas.SetTop(this, _dragOrigin.Y + offset.Y);
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
            return;
        }

        if (e.Key == Key.Enter &&
            (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) ==
            System.Windows.Input.ModifierKeys.Control)
        {
            EndEdit();
            e.Handled = true;
        }
    }
}
