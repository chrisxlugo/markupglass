using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MarkupGlass.Utilities;
using Point = System.Windows.Point;

namespace MarkupGlass;

public partial class ToolbarWindow : Window
{
    private readonly List<FrameworkElement> _interactiveElements = new();
    private HwndSource? _source;

    public ToolbarWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => SetFullScreen();
    }

    public void AttachLayers(IEnumerable<UIElement> layers)
    {
        foreach (var layer in layers)
        {
            ToolbarRoot.Children.Add(layer);
        }
    }

    public void SetInteractiveElements(IEnumerable<FrameworkElement> elements)
    {
        _interactiveElements.Clear();
        _interactiveElements.AddRange(elements);
    }

    public void SetLayerVisibility(UIElement layer, bool visible)
    {
        layer.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            _source = source;
            _source.AddHook(WndProc);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_source != null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }

        base.OnClosed(e);
    }

    private void SetFullScreen()
    {
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_NCHITTEST)
        {
            var screenPoint = GetScreenPointFromLParam(lParam);
            handled = true;
            return IsPointOverInteractiveUi(screenPoint)
                ? new IntPtr(Win32.HTCLIENT)
                : new IntPtr(Win32.HTTRANSPARENT);
        }

        return IntPtr.Zero;
    }

    private static Point GetScreenPointFromLParam(IntPtr lParam)
    {
        var value = lParam.ToInt64();
        var x = unchecked((short)(value & 0xFFFF));
        var y = unchecked((short)((value >> 16) & 0xFFFF));
        return new Point(x, y);
    }

    private bool IsPointOverInteractiveUi(Point screenPoint)
    {
        if (!IsVisible)
        {
            return false;
        }

        var windowPoint = PointFromScreen(screenPoint);
        var hit = VisualTreeHelper.HitTest(this, windowPoint);
        if (hit?.VisualHit is not DependencyObject visual)
        {
            return false;
        }

        foreach (var element in _interactiveElements)
        {
            if (IsDescendantOf(visual, element))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDescendantOf(DependencyObject? child, DependencyObject parent)
    {
        var current = child;
        while (current != null)
        {
            if (ReferenceEquals(current, parent))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }
}
