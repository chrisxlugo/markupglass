using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Point = System.Windows.Point;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Cursors = System.Windows.Input.Cursors;
using Brushes = System.Windows.Media.Brushes;
using SystemColors = System.Windows.SystemColors;
using TextBox = System.Windows.Controls.TextBox;
using MessageBox = System.Windows.MessageBox;
using ColorConverter = System.Windows.Media.ColorConverter;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Diagnostics;
using Drawing = System.Drawing;
using DrawingImaging = System.Drawing.Imaging;
using Forms = System.Windows.Forms;
using MarkupGlass.Controls;
using MarkupGlass.Models;
using MarkupGlass.Services;
using MarkupGlass.Utilities;

namespace MarkupGlass;

public partial class MainWindow : Window
{
    private readonly SessionStore _sessionStore = new();
    private readonly SettingsStore _settingsStore = new();
    private readonly UndoManager _undoManager;
    private readonly HotkeyManager _hotkeyManager = new();
    private readonly DispatcherTimer _saveTimer;
    private readonly List<ColorOption> _colors = new();
    private readonly List<ColorOption> _toolbarColors = new();
    private readonly Brush _activeToolBrush = SystemColors.HighlightBrush;
    private Color _selectedColor = Colors.White;
    private double _selectedFontSize = 16;
    private ToolMode _toolMode = ToolMode.Pen;
    private ToolMode _lastNonCursorTool = ToolMode.Pen;
    private bool _toolbarCollapsed;
    private bool _isRestoring;
    private Point _toolbarDragStart;
    private Point _toolbarOrigin;
    private bool _toolbarDragging;
    private TextAnnotationControl? _selectedText;
    private ShapeAnnotationControl? _selectedShape;
    private HwndSource? _source;
    private ToolbarWindow? _toolbarWindow;
    private bool _isPlacingShape;
    private Point _shapeStart;
    private ShapeAnnotationControl? _activeShape;
    private ShapeType _shapeType = ShapeType.Rectangle;
    private AppSettings _appSettings = new();
    private HotkeySettings _hotkeySettings = new();
    private bool _settingsDragging;
    private Point _settingsDragStart;
    private Point _settingsOrigin;
    private bool _moveTextMode;
    private bool _suppressTextCreateOnClick;
    private bool _moveTextChordHeld;
    private bool _clickThroughEnabled;
    private double _whiteboardWidth;
    private const double WhiteboardGap = 12;
    private readonly RectangleGeometry _whiteboardClip = new();
    private bool _whiteboardClipApplied;
    private bool _deferWhiteboardClip;
    private System.Windows.Size _toolbarDragSize;
    private System.Windows.Media.Effects.Effect? _whiteboardShadowEffect;
    private bool _whiteboardGhosting;
    private double _whiteboardGhostLeft;
    private double _whiteboardGhostTop;
    private double _whiteboardLeft;
    private double _whiteboardTop;
    private bool _whiteboardPositionInitialized;
    private readonly Stopwatch _toolbarDragClock = Stopwatch.StartNew();
    private long _lastToolbarDragTick;

    public MainWindow()
    {
        InitializeComponent();
        _appSettings = _settingsStore.Load();
        _hotkeySettings = _appSettings.Hotkeys;
        _undoManager = new UndoManager(_sessionStore);
        _saveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };
        _saveTimer.Tick += (_, _) => SaveSession();
        Loaded += OnLoaded;
        Closing += (_, _) => SaveSession();
        StateChanged += OnWindowStateChanged;

        InkSurface.StrokeCollected += (_, _) => ScheduleSave();
        InkSurface.Strokes.StrokesChanged += (_, _) => ScheduleSave();
        PreviewMouseLeftButtonDown += OnWindowPreviewMouseDown;
        PreviewMouseMove += OnWindowPreviewMouseMove;
        PreviewMouseLeftButtonUp += OnWindowPreviewMouseUp;
        PreviewKeyDown += OnWindowPreviewKeyDown;
        PreviewKeyUp += OnWindowPreviewKeyUp;

        HookToolbarDrag();
        HookToolbarButtons();
        Toolbar.SizeChanged += (_, _) => PositionWhiteboard();
        SetupPickers();
        UpdateShapeType(_shapeType);
        UpdateInkToolIcon(_toolMode);
        InitializeHotkeyInputs();
        HookSettingsDrag();
        SetTool(ToolMode.Pen);
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            SetTool(ToolMode.Cursor);
            if (WhiteboardPanel.Visibility == Visibility.Visible)
            {
                ToggleWhiteboard();
            }
            WhiteboardLayer.Visibility = Visibility.Collapsed;
            Hide();
            if (_toolbarWindow != null)
            {
                _toolbarWindow.Visibility = Visibility.Hidden;
                _toolbarWindow.SetLayerVisibility(WhiteboardLayer, false);
            }
            return;
        }

        if (Visibility != Visibility.Visible)
        {
            Show();
        }

        WhiteboardLayer.Visibility = Visibility.Visible;
        if (_toolbarWindow != null && Visibility == Visibility.Visible)
        {
            _toolbarWindow.Visibility = Visibility.Visible;
            _toolbarWindow.SetLayerVisibility(WhiteboardLayer, WhiteboardPanel.Visibility == Visibility.Visible);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SetFullScreen();
        InitializeToolbarWindow();
        PositionToolbarOnPrimary();
        LoadSession();
        _undoManager.Push(BuildSession());
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            _source = source;
            _source.AddHook(WndProc);
            _hotkeyManager.Initialize(source);
            RegisterHotkeys();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_source != null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }
        if (_toolbarWindow != null)
        {
            _toolbarWindow.Close();
            _toolbarWindow = null;
        }
        _hotkeyManager.Dispose();
        base.OnClosed(e);
    }

    private void RegisterHotkeys()
    {
        ApplyHotkeys();
    }

    private void ApplyHotkeys()
    {
        _hotkeyManager.Clear();
        RegisterHotkey(_hotkeySettings.ToggleCursor, ToggleCursorMode);
        RegisterHotkey(_hotkeySettings.ToggleVisibility, ToggleVisibility);
        RegisterHotkey(_hotkeySettings.CloseApp, Close);
        RegisterHotkey(_hotkeySettings.ClearAll, ClearAll);
        RegisterHotkey(_hotkeySettings.Undo, Undo);
        RegisterHotkey(_hotkeySettings.SelectPen, () => SetTool(ToolMode.Pen));
        RegisterHotkey(_hotkeySettings.SelectHighlighter, () => SetTool(ToolMode.Highlighter));
        RegisterHotkey(_hotkeySettings.SelectEraser, () => SetTool(ToolMode.Eraser));
        RegisterHotkey(_hotkeySettings.ShapeLine, () => ActivateShapeHotkey(ShapeType.Line));
        RegisterHotkey(_hotkeySettings.ShapeArrow, () => ActivateShapeHotkey(ShapeType.Arrow));
        RegisterHotkey(_hotkeySettings.ShapeRectangle, () => ActivateShapeHotkey(ShapeType.Rectangle));
        RegisterHotkey(_hotkeySettings.ShapeEllipse, () => ActivateShapeHotkey(ShapeType.Ellipse));
        RegisterHotkey(_hotkeySettings.ToggleColorPalette, ToggleColorPalette);
    }

    private void RegisterHotkey(HotkeyBinding binding, Action handler)
    {
        if (binding.Key == Key.None)
        {
            return;
        }

        var key = (uint)KeyInterop.VirtualKeyFromKey(binding.Key);
        try
        {
            _hotkeyManager.Register(binding.Modifiers, key, handler);
        }
        catch (InvalidOperationException)
        {
            // Hotkey is already in use by another app; skip to avoid crashing on startup.
        }
    }

    private void ApplyHotkeysAndSave()
    {
        try
        {
            ApplyHotkeys();
            SaveSettings();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to register hotkey: {ex.Message}", "Hotkey Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void InitializeHotkeyInputs()
    {
        SetupHotkeyBox(HotkeyCursorBox, HotkeyAction.ToggleCursor);
        SetupHotkeyBox(HotkeyVisibilityBox, HotkeyAction.ToggleVisibility);
        SetupHotkeyBox(HotkeyCloseBox, HotkeyAction.CloseApp);
        SetupHotkeyBox(HotkeyClearBox, HotkeyAction.ClearAll);
        SetupHotkeyBox(HotkeyUndoBox, HotkeyAction.Undo);
        SetupHotkeyBox(HotkeyPenBox, HotkeyAction.SelectPen);
        SetupHotkeyBox(HotkeyHighlighterBox, HotkeyAction.SelectHighlighter);
        SetupHotkeyBox(HotkeyEraserBox, HotkeyAction.SelectEraser);
        SetupHotkeyBox(HotkeyLineBox, HotkeyAction.ShapeLine);
        SetupHotkeyBox(HotkeyArrowBox, HotkeyAction.ShapeArrow);
        SetupHotkeyBox(HotkeyRectBox, HotkeyAction.ShapeRectangle);
        SetupHotkeyBox(HotkeyEllipseBox, HotkeyAction.ShapeEllipse);
        SetupHotkeyBox(HotkeyColorsBox, HotkeyAction.ToggleColorPalette);
        RefreshHotkeyDisplay();
    }

    private void SetupHotkeyBox(TextBox box, HotkeyAction action)
    {
        box.Tag = action;
        box.PreviewKeyDown += OnHotkeyBoxPreviewKeyDown;
        box.GotKeyboardFocus += (_, _) => box.SelectAll();
    }

    private void RefreshHotkeyDisplay()
    {
        HotkeyCursorBox.Text = FormatHotkey(_hotkeySettings.ToggleCursor);
        HotkeyVisibilityBox.Text = FormatHotkey(_hotkeySettings.ToggleVisibility);
        HotkeyCloseBox.Text = FormatHotkey(_hotkeySettings.CloseApp);
        HotkeyClearBox.Text = FormatHotkey(_hotkeySettings.ClearAll);
        HotkeyUndoBox.Text = FormatHotkey(_hotkeySettings.Undo);
        HotkeyPenBox.Text = FormatHotkey(_hotkeySettings.SelectPen);
        HotkeyHighlighterBox.Text = FormatHotkey(_hotkeySettings.SelectHighlighter);
        HotkeyEraserBox.Text = FormatHotkey(_hotkeySettings.SelectEraser);
        HotkeyLineBox.Text = FormatHotkey(_hotkeySettings.ShapeLine);
        HotkeyArrowBox.Text = FormatHotkey(_hotkeySettings.ShapeArrow);
        HotkeyRectBox.Text = FormatHotkey(_hotkeySettings.ShapeRectangle);
        HotkeyEllipseBox.Text = FormatHotkey(_hotkeySettings.ShapeEllipse);
        HotkeyColorsBox.Text = FormatHotkey(_hotkeySettings.ToggleColorPalette);
    }

    private void OnHotkeyBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || box.Tag is not HotkeyAction action)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.LeftCtrl || key == Key.RightCtrl ||
            key == Key.LeftShift || key == Key.RightShift ||
            key == Key.LeftAlt || key == Key.RightAlt ||
            key == Key.LWin || key == Key.RWin)
        {
            e.Handled = true;
            return;
        }

        var modifiers = GetModifierFlags(Keyboard.Modifiers);
        var binding = new HotkeyBinding { Modifiers = modifiers, Key = key };
        SetHotkey(action, binding);
        RefreshHotkeyDisplay();
        e.Handled = true;
    }

    private static uint GetModifierFlags(ModifierKeys modifiers)
    {
        uint flags = 0;
        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            flags |= 0x0001;
        }
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            flags |= 0x0002;
        }
        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            flags |= 0x0004;
        }
        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            flags |= 0x0008;
        }
        return flags;
    }

    private void SetHotkey(HotkeyAction action, HotkeyBinding binding)
    {
        switch (action)
        {
            case HotkeyAction.ToggleCursor:
                _hotkeySettings.ToggleCursor = binding;
                break;
            case HotkeyAction.ToggleVisibility:
                _hotkeySettings.ToggleVisibility = binding;
                break;
            case HotkeyAction.CloseApp:
                _hotkeySettings.CloseApp = binding;
                break;
            case HotkeyAction.ClearAll:
                _hotkeySettings.ClearAll = binding;
                break;
            case HotkeyAction.Undo:
                _hotkeySettings.Undo = binding;
                break;
            case HotkeyAction.SelectPen:
                _hotkeySettings.SelectPen = binding;
                break;
            case HotkeyAction.SelectHighlighter:
                _hotkeySettings.SelectHighlighter = binding;
                break;
            case HotkeyAction.SelectEraser:
                _hotkeySettings.SelectEraser = binding;
                break;
            case HotkeyAction.ShapeLine:
                _hotkeySettings.ShapeLine = binding;
                break;
            case HotkeyAction.ShapeArrow:
                _hotkeySettings.ShapeArrow = binding;
                break;
            case HotkeyAction.ShapeRectangle:
                _hotkeySettings.ShapeRectangle = binding;
                break;
            case HotkeyAction.ShapeEllipse:
                _hotkeySettings.ShapeEllipse = binding;
                break;
            case HotkeyAction.ToggleColorPalette:
                _hotkeySettings.ToggleColorPalette = binding;
                break;
        }
    }

    private static string FormatHotkey(HotkeyBinding binding)
    {
        if (binding.Key == Key.None)
        {
            return "Unassigned";
        }

        var parts = new List<string>();
        if ((binding.Modifiers & 0x0002) != 0)
        {
            parts.Add("Ctrl");
        }
        if ((binding.Modifiers & 0x0004) != 0)
        {
            parts.Add("Shift");
        }
        if ((binding.Modifiers & 0x0001) != 0)
        {
            parts.Add("Alt");
        }
        if ((binding.Modifiers & 0x0008) != 0)
        {
            parts.Add("Win");
        }
        parts.Add(binding.Key.ToString());
        return string.Join("+", parts);
    }

    private void ActivateShapeHotkey(ShapeType shapeType)
    {
        UpdateShapeType(shapeType);
        SetTool(ToolMode.Shapes);
    }

    private void ToggleColorPalette()
    {
        SetColorPaletteVisible(ColorPalette.Visibility != Visibility.Visible);
    }

    private void ToggleSettings()
    {
        SetSettingsVisible(SettingsPanel.Visibility != Visibility.Visible);
    }

    private void SetSettingsVisible(bool visible)
    {
        SettingsPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible)
        {
            SetWhiteboardVisible(false);
            RefreshHotkeyDisplay();
            Dispatcher.BeginInvoke(new Action(PositionSettingsPanel), DispatcherPriority.Loaded);
        }
    }

    private void HookSettingsDrag()
    {
        SettingsHeader.MouseLeftButtonDown += (s, e) =>
        {
            if (e.OriginalSource is Button)
            {
                return;
            }

            _settingsDragging = true;
            _settingsDragStart = e.GetPosition(UiLayer);
            _settingsOrigin = new Point(Canvas.GetLeft(SettingsPanel), Canvas.GetTop(SettingsPanel));
            SettingsHeader.CaptureMouse();
            e.Handled = true;
        };

        SettingsHeader.MouseMove += (s, e) =>
        {
            if (!_settingsDragging)
            {
                return;
            }

            var current = e.GetPosition(UiLayer);
            var offset = current - _settingsDragStart;
            Canvas.SetLeft(SettingsPanel, _settingsOrigin.X + offset.X);
            Canvas.SetTop(SettingsPanel, _settingsOrigin.Y + offset.Y);
        };

        SettingsHeader.MouseLeftButtonUp += (s, e) =>
        {
            if (!_settingsDragging)
            {
                return;
            }

            _settingsDragging = false;
            SettingsHeader.ReleaseMouseCapture();
            e.Handled = true;
        };
    }

    private void SetFullScreen()
    {
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
    }

    private void InitializeToolbarWindow()
    {
        if (_toolbarWindow != null)
        {
            return;
        }

        _toolbarWindow = new ToolbarWindow
        {
            Owner = this
        };
        _toolbarWindow.Show();

        if (Content is Grid root)
        {
            var layers = new UIElement[]
            {
                UiLayer,
                ShapePaletteLayer,
                InkPaletteLayer,
                SettingsLayer,
                ColorPaletteLayer,
                FontPaletteLayer,
            };

            foreach (var layer in layers)
            {
                root.Children.Remove(layer);
            }

            _toolbarWindow.AttachLayers(layers);
            _toolbarWindow.SetInteractiveElements(new FrameworkElement[]
            {
                Toolbar,
                ShapePalette,
                InkPalette,
                ColorPalette,
                FontPalette,
                SettingsPanel,
                WhiteboardResizeThumb
            });
        }
    }

    private void PositionToolbarOnPrimary()
    {
        var primary = Forms.Screen.PrimaryScreen?.Bounds
            ?? Forms.Screen.AllScreens.FirstOrDefault()?.Bounds
            ?? new Drawing.Rectangle(0, 0, (int)SystemParameters.PrimaryScreenWidth,
                (int)SystemParameters.PrimaryScreenHeight);
        var left = primary.Left - SystemParameters.VirtualScreenLeft + 20;
        var top = primary.Top - SystemParameters.VirtualScreenTop + 20;
        Canvas.SetLeft(Toolbar, left);
        Canvas.SetTop(Toolbar, top);
        PositionWhiteboard();
    }

    private void PositionSettingsPanel()
    {
        var panelWidth = SettingsPanel.Width;
        var panelHeight = SettingsPanel.Height;
        if (panelWidth <= 0 || panelHeight <= 0)
        {
            SettingsPanel.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            if (panelWidth <= 0)
            {
                panelWidth = SettingsPanel.DesiredSize.Width;
            }
            if (panelHeight <= 0)
            {
                panelHeight = SettingsPanel.DesiredSize.Height;
            }
        }

        var toolbarLeft = Canvas.GetLeft(Toolbar);
        var toolbarTop = Canvas.GetTop(Toolbar);
        if (double.IsNaN(toolbarLeft))
        {
            toolbarLeft = 20;
        }
        if (double.IsNaN(toolbarTop))
        {
            toolbarTop = 20;
        }

        const double padding = 20;
        const double gap = 16;
        var left = toolbarLeft + Toolbar.ActualWidth + gap;
        var top = toolbarTop;

        var maxLeft = Math.Max(0, ActualWidth - panelWidth - padding);
        var maxTop = Math.Max(0, ActualHeight - panelHeight - padding);
        if (left > maxLeft)
        {
            left = toolbarLeft - panelWidth - gap;
        }

        left = Math.Clamp(left, padding, maxLeft);
        top = Math.Clamp(top, padding, maxTop);
        Canvas.SetLeft(SettingsPanel, left);
        Canvas.SetTop(SettingsPanel, top);
    }

    private void HookToolbarDrag()
    {
        Toolbar.MouseLeftButtonDown += (_, e) =>
        {
            _toolbarDragging = true;
            _toolbarDragStart = e.GetPosition(UiLayer);
            _toolbarOrigin = new Point(Canvas.GetLeft(Toolbar), Canvas.GetTop(Toolbar));
            _toolbarDragSize = GetToolbarSize();
            _deferWhiteboardClip = WhiteboardPanel.Visibility == Visibility.Visible;
            if (_deferWhiteboardClip)
            {
                _whiteboardShadowEffect ??= WhiteboardPanel.Effect;
                WhiteboardPanel.Effect = null;
                WhiteboardPanel.CacheMode = new BitmapCache(1.0);
                BeginWhiteboardGhost();
            }
            Toolbar.CacheMode = new BitmapCache(1.0);
            _lastToolbarDragTick = _toolbarDragClock.ElapsedMilliseconds;
            Toolbar.CaptureMouse();
        };

        Toolbar.MouseMove += (_, e) =>
        {
            if (!_toolbarDragging)
            {
                return;
            }

            var now = _toolbarDragClock.ElapsedMilliseconds;
            if (now - _lastToolbarDragTick < 16)
            {
                return;
            }
            _lastToolbarDragTick = now;

            var current = e.GetPosition(UiLayer);
            var offset = current - _toolbarDragStart;
            Canvas.SetLeft(Toolbar, _toolbarOrigin.X + offset.X);
            Canvas.SetTop(Toolbar, _toolbarOrigin.Y + offset.Y);
            PositionPalettes();
            if (_whiteboardGhosting)
            {
                UpdateWhiteboardGhost();
            }
            else if (WhiteboardPanel.Visibility != Visibility.Visible)
            {
                PositionWhiteboard(updateClip: !_deferWhiteboardClip);
            }
        };

        Toolbar.MouseLeftButtonUp += (_, _) =>
        {
            if (!_toolbarDragging)
            {
                return;
            }

            _toolbarDragging = false;
            _deferWhiteboardClip = false;
            Toolbar.ReleaseMouseCapture();
            PositionPalettes();
            if (_whiteboardGhosting)
            {
                CommitWhiteboardGhost();
            }
            PositionWhiteboard();
            if (_whiteboardShadowEffect != null)
            {
                WhiteboardPanel.Effect = _whiteboardShadowEffect;
            }
            WhiteboardPanel.CacheMode = null;
            Toolbar.CacheMode = null;
        };
    }

    private void HookToolbarButtons()
    {
        ToolbarToggleButton.Click += (_, _) => ToggleToolbar();
        CursorButton.Click += (_, _) => SetTool(ToolMode.Cursor);
        PenButton.Click += (_, _) => SetTool(ToolMode.Pen);
        PenButton.PreviewMouseLeftButtonDown += OnPenButtonPreviewMouseDown;
        HighlighterButton.Click += (_, _) => SetTool(ToolMode.Highlighter);
        EraserButton.Click += (_, _) => SetTool(ToolMode.Eraser);
        TextButton.Click += (_, _) => SetTool(ToolMode.Text);
        ShapeButton.Click += (_, _) => SetTool(ToolMode.Shapes);
        ShapeButton.PreviewMouseLeftButtonDown += OnShapeButtonPreviewMouseDown;
        ColorButton.PreviewMouseLeftButtonDown += OnColorButtonPreviewMouseDown;
        FontButton.PreviewMouseLeftButtonDown += OnFontButtonPreviewMouseDown;
        WhiteboardButton.Click += (_, _) => ToggleWhiteboard();
        SettingsButton.Click += (_, _) => ToggleSettings();
        CloseSettingsButton.Click += (_, _) => SetSettingsVisible(false);
        CloseSettingsHeaderButton.Click += (_, _) => SetSettingsVisible(false);
        ApplyHotkeysButton.Click += (_, _) => ApplyHotkeysAndSave();
        UndoButton.Click += (_, _) => Undo();
        ClearButton.Click += (_, _) => ClearAll();
        TextBackgroundToggle.Checked += (_, _) => UpdateSelectedTextStyle();
        TextBackgroundToggle.Unchecked += (_, _) => UpdateSelectedTextStyle();

        WhiteboardResizeThumb.DragStarted += (_, _) =>
        {
            if (_whiteboardWidth <= 0)
            {
                _whiteboardWidth = GetToolbarSize().Width;
            }
        };
        WhiteboardResizeThumb.DragDelta += (_, e) =>
        {
            var minWidth = GetToolbarSize().Width;
            _whiteboardWidth = Math.Max(minWidth, _whiteboardWidth + e.HorizontalChange);
            PositionWhiteboard();
            e.Handled = true;
        };

        LineShapeButton.PreviewMouseLeftButtonDown += (s, e) => HandleShapePaletteClick(ShapeType.Line, e);
        ArrowShapeButton.PreviewMouseLeftButtonDown += (s, e) => HandleShapePaletteClick(ShapeType.Arrow, e);
        RectShapeButton.PreviewMouseLeftButtonDown += (s, e) => HandleShapePaletteClick(ShapeType.Rectangle, e);
        EllipseShapeButton.PreviewMouseLeftButtonDown += (s, e) => HandleShapePaletteClick(ShapeType.Ellipse, e);

        PenInkButton.PreviewMouseLeftButtonDown += (s, e) => HandleInkPaletteClick(ToolMode.Pen, e);
        HighlighterInkButton.PreviewMouseLeftButtonDown += (s, e) => HandleInkPaletteClick(ToolMode.Highlighter, e);
    }

    private void SetupPickers()
    {
        LoadColorSettings();
        PopulateColorPickers();

        var fontSizes = new List<double> { 12, 16, 20, 24, 32 };
        _selectedFontSize = fontSizes[1];
        UpdateFontSizeLabel();
        foreach (var size in fontSizes)
        {
            FontPalettePanel.Children.Add(CreateFontPaletteButton(size));
        }

        ThicknessSlider.ValueChanged += (_, _) => UpdateDrawingAttributes();
    }

    private void LoadColorSettings()
    {
        _colors.Clear();
        _toolbarColors.Clear();

        var defaults = new[]
        {
            Colors.White,
            Colors.Red,
            Colors.Yellow,
            Colors.Aqua,
            Colors.Lime
        };

        var library = _appSettings.ColorLibrary;
        if (library == null || library.Count == 0)
        {
            foreach (var color in defaults)
            {
                _colors.Add(new ColorOption(color.ToString(), color));
            }
        }
        else
        {
            foreach (var value in library)
            {
                if (TryParseColor(value, out var color))
                {
                    _colors.Add(new ColorOption(color.ToString(), color));
                }
            }
        }

        if (_colors.Count == 0)
        {
            foreach (var color in defaults)
            {
                _colors.Add(new ColorOption(color.ToString(), color));
            }
        }

        var toolbar = _appSettings.ToolbarColors;
        if (toolbar == null || toolbar.Count == 0)
        {
            foreach (var color in defaults)
            {
                _toolbarColors.Add(new ColorOption(color.ToString(), color));
            }
        }
        else
        {
            foreach (var value in toolbar.Take(5))
            {
                if (TryParseColor(value, out var color))
                {
                    _toolbarColors.Add(new ColorOption(color.ToString(), color));
                }
            }
        }

        while (_toolbarColors.Count < 5)
        {
            var color = defaults[_toolbarColors.Count % defaults.Length];
            _toolbarColors.Add(new ColorOption(color.ToString(), color));
        }

        EnsureToolbarColorsInLibrary();

        _selectedColor = _toolbarColors[0].Color;
        UpdateColorSwatch();
        BuildColorPaletteButtons();
        RefreshColorLibraryPanel();
    }

    private void PopulateColorPickers()
    {
        var combos = new[]
        {
            ToolbarColor1Box,
            ToolbarColor2Box,
            ToolbarColor3Box,
            ToolbarColor4Box,
            ToolbarColor5Box
        };

        for (var i = 0; i < combos.Length; i++)
        {
            combos[i].ItemsSource = _colors;
            combos[i].SelectedItem = _toolbarColors[i];
            combos[i].Tag = i;
            combos[i].SelectionChanged += OnToolbarColorSelectionChanged;
        }

        AddColorButton.Click += (_, _) => AddColorFromInputs();
    }

    private void EnsureToolbarColorsInLibrary()
    {
        for (var i = 0; i < _toolbarColors.Count; i++)
        {
            var option = _toolbarColors[i];
            var existing = _colors.FirstOrDefault(entry => entry.Color == option.Color);
            if (existing != null)
            {
                _toolbarColors[i] = existing;
                continue;
            }

            _colors.Add(option);
        }
    }

    private void BuildColorPaletteButtons()
    {
        ColorPalettePanel.Children.Clear();
        foreach (var option in _toolbarColors)
        {
            ColorPalettePanel.Children.Add(CreateColorPaletteButton(option));
        }
    }

    private void RefreshColorLibraryPanel()
    {
        ColorLibraryPanel.Children.Clear();
        foreach (var option in _colors)
        {
            ColorLibraryPanel.Children.Add(CreateLibraryColorButton(option));
        }
    }

    private void AddColorFromInputs()
    {
        if (!TryParseRgbBox(ColorRedBox.Text, out var r) ||
            !TryParseRgbBox(ColorGreenBox.Text, out var g) ||
            !TryParseRgbBox(ColorBlueBox.Text, out var b))
        {
            MessageBox.Show("Enter RGB values from 0 to 255.", "Invalid Color",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var color = Color.FromRgb((byte)r, (byte)g, (byte)b);
        if (_colors.Any(option => option.Color == color))
        {
            return;
        }

        var option = new ColorOption(color.ToString(), color);
        _colors.Add(option);
        RefreshColorLibraryPanel();
        RefreshToolbarColorPickers();
        SaveSettings();
    }

    private void RefreshToolbarColorPickers()
    {
        var combos = new[]
        {
            ToolbarColor1Box,
            ToolbarColor2Box,
            ToolbarColor3Box,
            ToolbarColor4Box,
            ToolbarColor5Box
        };

        for (var i = 0; i < combos.Length; i++)
        {
            var combo = combos[i];
            combo.ItemsSource = null;
            combo.ItemsSource = _colors;
            combo.SelectedItem = _toolbarColors[i];
        }
    }

    private void OnToolbarColorSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox combo || combo.Tag is not int index)
        {
            return;
        }

        if (combo.SelectedItem is not ColorOption option)
        {
            return;
        }

        _toolbarColors[index] = option;
        BuildColorPaletteButtons();
        SaveSettings();
    }

    private Button CreateLibraryColorButton(ColorOption option)
    {
        var button = new Button
        {
            Style = (Style)FindResource("ToolButtonStyle"),
            Width = 32,
            Height = 32,
            Margin = new Thickness(0, 4, 6, 4),
            Content = new System.Windows.Shapes.Rectangle
            {
                Width = 16,
                Height = 16,
                RadiusX = 4,
                RadiusY = 4,
                Stroke = new SolidColorBrush(Color.FromArgb(34, 0, 0, 0)),
                Fill = new SolidColorBrush(option.Color)
            }
        };

        button.Click += (_, _) =>
        {
            _selectedColor = option.Color;
            UpdateColorSwatch();
            UpdateDrawingAttributes();
            UpdateSelectedTextStyle();
        };

        return button;
    }

    private static bool TryParseRgbBox(string value, out int channel)
    {
        if (int.TryParse(value, out channel))
        {
            return channel >= 0 && channel <= 255;
        }

        return false;
    }

    private static bool TryParseColor(string value, out Color color)
    {
        try
        {
            var parsed = (Color)ColorConverter.ConvertFromString(value)!;
            color = Color.FromArgb(255, parsed.R, parsed.G, parsed.B);
            return true;
        }
        catch (FormatException)
        {
            color = Colors.White;
            return false;
        }
    }

    private void SaveSettings()
    {
        _appSettings.Hotkeys = _hotkeySettings;
        _appSettings.ColorLibrary = _colors
            .Select(option => option.Color.ToString())
            .ToList();
        _appSettings.ToolbarColors = _toolbarColors
            .Select(option => option.Color.ToString())
            .ToList();
        _settingsStore.Save(_appSettings);
    }

    private void UpdateDrawingAttributes()
    {
        if (_toolMode == ToolMode.Shapes)
        {
            UpdateSelectedShapeStyle(_selectedColor, ThicknessSlider.Value);
        }

        var color = _selectedColor;
        var thickness = ThicknessSlider.Value;
        var attrs = new DrawingAttributes
        {
            Color = color,
            Width = thickness,
            Height = thickness,
            FitToCurve = true,
            IsHighlighter = _toolMode == ToolMode.Highlighter
        };

        if (_toolMode == ToolMode.Highlighter)
        {
            attrs.Color = Color.FromArgb(120, color.R, color.G, color.B);
        }

        InkSurface.DefaultDrawingAttributes = attrs;
    }

    private void SetTool(ToolMode mode)
    {
        _toolMode = mode;
        DeselectText();
        DeselectShape();
        if (mode != ToolMode.Text)
        {
            SetMoveTextMode(false);
        }
        if (mode != ToolMode.Cursor)
        {
            _lastNonCursorTool = mode;
        }

        var interactive = mode != ToolMode.Cursor;
        InkSurface.IsHitTestVisible = interactive && mode != ToolMode.Text && mode != ToolMode.Shapes;
        InkSurface.IsEnabled = interactive && mode != ToolMode.Text && mode != ToolMode.Shapes;
        ShapeLayer.IsHitTestVisible = mode == ToolMode.Shapes;
        TextLayer.IsHitTestVisible = mode == ToolMode.Text;
        UiLayer.IsHitTestVisible = true;
        Toolbar.IsHitTestVisible = true;
        Toolbar.Opacity = 1.0;
        SetClickThrough(mode == ToolMode.Cursor);

        switch (mode)
        {
            case ToolMode.Pen:
            case ToolMode.Highlighter:
                InkSurface.EditingMode = InkCanvasEditingMode.Ink;
                break;
            case ToolMode.Eraser:
                InkSurface.EditingMode = InkCanvasEditingMode.EraseByStroke;
                break;
            case ToolMode.Text:
                InkSurface.EditingMode = InkCanvasEditingMode.None;
                break;
            case ToolMode.Shapes:
                InkSurface.EditingMode = InkCanvasEditingMode.None;
                break;
            case ToolMode.Cursor:
                InkSurface.EditingMode = InkCanvasEditingMode.None;
                break;
        }

        SetCursorForTool(mode);
        UpdateDrawingAttributes();
        SetActiveToolButton(mode);
        UpdateInkToolIcon(mode);

        if (interactive)
        {
            Activate();
            Focus();
            InkSurface.Focus();
        }
    }

    private void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        var positionInUiLayer = e.GetPosition(UiLayer);
        if (ShouldRestrictToWhiteboard(source, positionInUiLayer))
        {
            e.Handled = true;
            return;
        }

        if (_toolMode == ToolMode.Text)
        {
            var moveHeld = IsMoveTextChordHeld();
            if (!moveHeld)
            {
                var altHeld = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
                var overText = FindAncestor<TextAnnotationControl>(e.OriginalSource as DependencyObject) != null;
                moveHeld = altHeld && overText;
            }
            if (moveHeld != _moveTextMode)
            {
                SetMoveTextMode(moveHeld);
            }
        }

        if (_selectedText != null && _selectedText.IsEditing)
        {
            var clickedText = FindAncestor<TextAnnotationControl>(e.OriginalSource as DependencyObject);
            if (!ReferenceEquals(clickedText, _selectedText))
            {
                _selectedText.EndEdit();
                Keyboard.ClearFocus();
                SetCursorForTool(_toolMode);
                Focus();
                if (_selectedText.IsMouseCaptured)
                {
                    _selectedText.ReleaseMouseCapture();
                }
                _suppressTextCreateOnClick = true;
            }
        }
        else if (_selectedText != null && _selectedText.IsMouseCaptured)
        {
            var clickedText = FindAncestor<TextAnnotationControl>(e.OriginalSource as DependencyObject);
            if (!ReferenceEquals(clickedText, _selectedText))
            {
                _selectedText.ReleaseMouseCapture();
            }
        }

        if (_toolMode == ToolMode.Eraser && e.LeftButton == MouseButtonState.Pressed)
        {
            var removed = TryEraseText(e.GetPosition(TextLayer)) || TryEraseShape(e.GetPosition(ShapeLayer));
            if (removed)
            {
                ScheduleSave();
                e.Handled = true;
            }
            return;
        }

        if (_toolMode == ToolMode.Shapes && e.LeftButton == MouseButtonState.Pressed)
        {
            DeselectText();
            var existingShape = FindAncestor<ShapeAnnotationControl>(e.OriginalSource as DependencyObject);
            if (existingShape != null)
            {
                SelectShape(existingShape);
                return;
            }

            DeselectShape();
            var shapePosition = e.GetPosition(ShapeLayer);
            var shape = CreateShapeControl();
            shape.SetAbsolutePoints(shapePosition, shapePosition);
            ShapeLayer.Children.Add(shape);
            SelectShape(shape);
            _activeShape = shape;
            _shapeStart = shapePosition;
            _isPlacingShape = true;
            CaptureMouse();
            e.Handled = true;
            return;
        }

        if (_toolMode != ToolMode.Text || _moveTextMode || e.LeftButton != MouseButtonState.Pressed)
        {
            if (FindAncestor<TextAnnotationControl>(e.OriginalSource as DependencyObject) == null)
            {
                DeselectText();
            }
            return;
        }

        var existingText = FindAncestor<TextAnnotationControl>(e.OriginalSource as DependencyObject);
        if (existingText != null)
        {
            SelectText(existingText);
            if (e.ClickCount == 2)
            {
                existingText.BeginEdit();
                e.Handled = true;
            }
            return;
        }

        if (_suppressTextCreateOnClick)
        {
            _suppressTextCreateOnClick = false;
            return;
        }

        var position = e.GetPosition(TextLayer);
        var control = CreateTextControl();
        Canvas.SetLeft(control, position.X);
        Canvas.SetTop(control, position.Y);
        TextLayer.Children.Add(control);
        SelectText(control);
        control.BeginEdit();
        ScheduleSave();
        e.Handled = true;
    }

    private void OnWindowPreviewMouseMove(object sender, MouseEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        var positionInUiLayer = e.GetPosition(UiLayer);
        if (!_isPlacingShape && ShouldRestrictToWhiteboard(source, positionInUiLayer))
        {
            return;
        }

        if (_toolMode == ToolMode.Text)
        {
            var moveHeld = IsMoveTextChordHeld();
            if (moveHeld != _moveTextMode)
            {
                SetMoveTextMode(moveHeld);
            }
        }

        if (_selectedText != null && _selectedText.IsEditing)
        {
            var position = e.GetPosition(TextLayer);
            if (!IsPointWithinControl(_selectedText, position))
            {
                _selectedText.EndEdit();
                Keyboard.ClearFocus();
                SetCursorForTool(_toolMode);
                Focus();
                if (_selectedText.IsMouseCaptured)
                {
                    _selectedText.ReleaseMouseCapture();
                }
                _suppressTextCreateOnClick = true;
            }
        }

        if (_toolMode == ToolMode.Eraser && e.LeftButton == MouseButtonState.Pressed)
        {
            var removed = TryEraseText(e.GetPosition(TextLayer)) || TryEraseShape(e.GetPosition(ShapeLayer));
            if (removed)
            {
                ScheduleSave();
                e.Handled = true;
                return;
            }
        }

        if (_isPlacingShape && IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed)
        {
            var shapeCurrent = e.GetPosition(ShapeLayer);
            _activeShape?.SetAbsolutePoints(_shapeStart, shapeCurrent);
            e.Handled = true;
            return;
        }

    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_selectedText != null && _selectedText.IsEditing)
            {
                _selectedText.EndEdit();
                Focus();
                e.Handled = true;
            }

            return;
        }

        if (_toolMode == ToolMode.Text && IsMoveTextChordKey(e))
        {
            _moveTextChordHeld = true;
            SetMoveTextMode(true);
            e.Handled = true;
        }

    }

    private void OnWindowPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (_toolMode == ToolMode.Text && IsMoveTextChordRelease(e))
        {
            _moveTextChordHeld = false;
            SetMoveTextMode(false);
            e.Handled = true;
        }
    }

    private static bool IsMoveTextChordKey(KeyEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            return false;
        }

        return e.Key == Key.X || (e.Key == Key.System && e.SystemKey == Key.X);
    }

    private static bool IsMoveTextChordRelease(KeyEventArgs e)
    {
        if (e.Key == Key.X || (e.Key == Key.System && e.SystemKey == Key.X))
        {
            return true;
        }

        return e.Key == Key.LeftAlt || e.Key == Key.RightAlt ||
               (e.Key == Key.System && (e.SystemKey == Key.LeftAlt || e.SystemKey == Key.RightAlt));
    }

    private bool IsMoveTextChordHeld()
    {
        if (_moveTextChordHeld)
        {
            return true;
        }

        var altDown = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) ||
            Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt);
        if (altDown && Keyboard.IsKeyDown(Key.X))
        {
            return true;
        }

        return false;
    }

    private void SetMoveTextMode(bool enabled)
    {
        if (_moveTextMode == enabled)
        {
            return;
        }

        _moveTextMode = enabled;
        if (_moveTextMode && _selectedText != null && _selectedText.IsEditing)
        {
            _selectedText.EndEdit();
        }

        foreach (var control in TextLayer.Children.OfType<TextAnnotationControl>())
        {
            control.CanEdit = !_moveTextMode;
            control.CanDrag = _moveTextMode;
            if (_moveTextMode && control.IsEditing)
            {
                control.EndEdit();
            }
            if (!enabled && control.IsMouseCaptured)
            {
                control.ReleaseMouseCapture();
            }
        }

        TextLayer.IsHitTestVisible = _toolMode == ToolMode.Text;
    }

    private async void OnWindowPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        var positionInUiLayer = e.GetPosition(UiLayer);
        if (!_isPlacingShape && ShouldRestrictToWhiteboard(source, positionInUiLayer))
        {
            return;
        }

        if (_toolMode == ToolMode.Text && _moveTextMode && !IsMoveTextChordHeld())
        {
            SetMoveTextMode(false);
        }

        if (_isPlacingShape && IsMouseCaptured && e.ChangedButton == MouseButton.Left)
        {
            ReleaseMouseCapture();
            _isPlacingShape = false;
            _activeShape = null;
            ScheduleSave();
            e.Handled = true;
            return;
        }

    }

    private TextAnnotationControl CreateTextControl()
    {
        var control = new TextAnnotationControl();
        control.Width = 200;
        control.Height = 60;
        control.FontSizeValue = _selectedFontSize;
        control.TextBrush = new SolidColorBrush(_selectedColor);
        control.HasBackground = TextBackgroundToggle.IsChecked == true;
        control.CanEdit = !_moveTextMode;
        control.CanDrag = _moveTextMode;
        control.Selected += (_, _) => SelectText(control);
        control.DragCompleted += (_, _) => ScheduleSave();
        control.ResizeCompleted += (_, _) => ScheduleSave();
        control.EditCompleted += (_, _) => ScheduleSave();
        return control;
    }

    private ShapeAnnotationControl CreateShapeControl()
    {
        var control = new ShapeAnnotationControl
        {
            ShapeType = _shapeType,
            Stroke = new SolidColorBrush(_selectedColor),
            StrokeThickness = ThicknessSlider.Value
        };
        control.Selected += (_, _) => SelectShape(control);
        control.DragCompleted += (_, _) => ScheduleSave();
        control.ResizeCompleted += (_, _) => ScheduleSave();
        return control;
    }

    private void SelectShape(ShapeAnnotationControl control)
    {
        if (_selectedShape == control)
        {
            return;
        }

        DeselectShape();
        _selectedShape = control;
        _selectedShape.SetSelected(true);
        if (_selectedShape.Stroke is SolidColorBrush brush)
        {
            _selectedColor = brush.Color;
            UpdateColorSwatch();
        }
        ThicknessSlider.Value = _selectedShape.StrokeThickness;
    }

    private void DeselectShape()
    {
        if (_selectedShape == null)
        {
            return;
        }

        _selectedShape.SetSelected(false);
        _selectedShape = null;
    }

    private void UpdateShapeType(ShapeType shapeType)
    {
        _shapeType = shapeType;
        LineShapeButton.IsChecked = shapeType == ShapeType.Line;
        ArrowShapeButton.IsChecked = shapeType == ShapeType.Arrow;
        RectShapeButton.IsChecked = shapeType == ShapeType.Rectangle;
        EllipseShapeButton.IsChecked = shapeType == ShapeType.Ellipse;

        ShapeIconLine.Visibility = shapeType == ShapeType.Line ? Visibility.Visible : Visibility.Collapsed;
        ShapeIconArrow.Visibility = shapeType == ShapeType.Arrow ? Visibility.Visible : Visibility.Collapsed;
        ShapeIconRect.Visibility = shapeType == ShapeType.Rectangle ? Visibility.Visible : Visibility.Collapsed;
        ShapeIconEllipse.Visibility = shapeType == ShapeType.Ellipse ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSelectedShapeStyle(Color color, double thickness)
    {
        if (_selectedShape == null)
        {
            return;
        }

        _selectedShape.Stroke = new SolidColorBrush(color);
        _selectedShape.StrokeThickness = thickness;
        ScheduleSave();
    }

    private void HandleShapePaletteClick(ShapeType shapeType, MouseButtonEventArgs e)
    {
        UpdateShapeType(shapeType);
        if (e.ClickCount == 2)
        {
            SetTool(ToolMode.Shapes);
            SetShapePaletteVisible(true);
            e.Handled = true;
            return;
        }

        e.Handled = true;
    }

    private void OnShapeButtonPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2)
        {
            return;
        }

        SetShapePaletteVisible(ShapePalette.Visibility != Visibility.Visible);
        e.Handled = true;
    }

    private void OnPenButtonPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2)
        {
            return;
        }

        SetInkPaletteVisible(InkPalette.Visibility != Visibility.Visible);
        e.Handled = true;
    }

    private void SetShapePaletteVisible(bool visible)
    {
        if (visible)
        {
            SetInkPaletteVisible(false);
            SetColorPaletteVisible(false);
            SetFontPaletteVisible(false);
        }

        ShapePalette.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible)
        {
            PositionPalettes();
        }
    }

    private void SetInkPaletteVisible(bool visible)
    {
        if (visible)
        {
            SetShapePaletteVisible(false);
            SetColorPaletteVisible(false);
            SetFontPaletteVisible(false);
        }

        InkPalette.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible)
        {
            PositionPalettes();
        }
    }

    private void HandleInkPaletteClick(ToolMode mode, MouseButtonEventArgs e)
    {
        UpdateInkToolIcon(mode);
        if (e.ClickCount == 2)
        {
            SetTool(mode);
            SetInkPaletteVisible(true);
            e.Handled = true;
            return;
        }

        e.Handled = true;
    }

    private void OnColorButtonPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2)
        {
            return;
        }

        SetColorPaletteVisible(ColorPalette.Visibility != Visibility.Visible);
        e.Handled = true;
    }

    private void OnFontButtonPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2)
        {
            return;
        }

        SetFontPaletteVisible(FontPalette.Visibility != Visibility.Visible);
        e.Handled = true;
    }

    private void SetColorPaletteVisible(bool visible)
    {
        if (visible)
        {
            SetShapePaletteVisible(false);
            SetInkPaletteVisible(false);
            SetFontPaletteVisible(false);
        }

        ColorPalette.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible)
        {
            PositionPalettes();
        }
    }

    private void SetFontPaletteVisible(bool visible)
    {
        if (visible)
        {
            SetShapePaletteVisible(false);
            SetInkPaletteVisible(false);
            SetColorPaletteVisible(false);
        }

        FontPalette.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible)
        {
            PositionPalettes();
        }
    }

    private void PositionPalettes()
    {
        var left = Canvas.GetLeft(Toolbar);
        var top = Canvas.GetTop(Toolbar);
        if (double.IsNaN(left))
        {
            left = 0;
        }
        if (double.IsNaN(top))
        {
            top = 0;
        }

        var baseLeft = left + Toolbar.ActualWidth + 12;
        Canvas.SetLeft(ShapePalette, baseLeft);
        Canvas.SetTop(ShapePalette, top + 0);

        Canvas.SetLeft(InkPalette, baseLeft);
        Canvas.SetTop(InkPalette, top + 120);

        Canvas.SetLeft(ColorPalette, baseLeft);
        Canvas.SetTop(ColorPalette, top + 220);

        Canvas.SetLeft(FontPalette, baseLeft);
        Canvas.SetTop(FontPalette, top + 320);
    }

    private void ToggleWhiteboard()
    {
        SetWhiteboardVisible(WhiteboardPanel.Visibility != Visibility.Visible);
    }

    private void SetWhiteboardVisible(bool visible)
    {
        WhiteboardPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        WhiteboardResizeThumb.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        WhiteboardGhost.Visibility = Visibility.Collapsed;
        _whiteboardGhosting = false;
        if (visible)
        {
            EnsureWhiteboardSize();
            PositionWhiteboard();
        }
        else
        {
            UpdateWhiteboardClip();
        }
    }

    private void EnsureWhiteboardSize()
    {
        var toolbarSize = GetToolbarSize();
        if (_whiteboardWidth <= 0)
        {
            _whiteboardWidth = toolbarSize.Width;
        }
        _whiteboardWidth = Math.Max(toolbarSize.Width, _whiteboardWidth);
    }

    private System.Windows.Size GetToolbarSize()
    {
        var width = Toolbar.ActualWidth;
        var height = Toolbar.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            Toolbar.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            if (width <= 0)
            {
                width = Toolbar.DesiredSize.Width;
            }
            if (height <= 0)
            {
                height = Toolbar.DesiredSize.Height;
            }
        }
        if (width <= 0)
        {
            width = Toolbar.Width;
        }
        if (height <= 0)
        {
            height = Toolbar.Height;
        }
        return new System.Windows.Size(width, height);
    }

    private void PositionWhiteboard(bool updateClip = true)
    {
        if (WhiteboardPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        var left = Canvas.GetLeft(Toolbar);
        var top = Canvas.GetTop(Toolbar);
        if (double.IsNaN(left))
        {
            left = 0;
        }
        if (double.IsNaN(top))
        {
            top = 0;
        }

        var toolbarSize = _toolbarDragging ? _toolbarDragSize : GetToolbarSize();
        var height = toolbarSize.Height;
        _whiteboardWidth = Math.Max(toolbarSize.Width, _whiteboardWidth);

        var boardLeft = left + toolbarSize.Width + WhiteboardGap;
        var boardTop = top;

        PositionWhiteboardAt(boardLeft, boardTop, _whiteboardWidth, height, updateClip);
    }

    private void PositionWhiteboardAt(double left, double top, double width, double height, bool updateClip)
    {
        if (!_whiteboardPositionInitialized)
        {
            _whiteboardLeft = left;
            _whiteboardTop = top;
            _whiteboardPositionInitialized = true;
        }
        else
        {
            var deltaX = left - _whiteboardLeft;
            var deltaY = top - _whiteboardTop;
            if (Math.Abs(deltaX) > 0.01 || Math.Abs(deltaY) > 0.01)
            {
                MoveWhiteboardContent(deltaX, deltaY);
                _whiteboardLeft = left;
                _whiteboardTop = top;
            }
        }

        WhiteboardPanel.Width = width;
        WhiteboardPanel.Height = height;
        Canvas.SetLeft(WhiteboardPanel, left);
        Canvas.SetTop(WhiteboardPanel, top);

        WhiteboardResizeThumb.Height = Math.Max(24, height - 16);
        Canvas.SetLeft(WhiteboardResizeThumb, left + width - (WhiteboardResizeThumb.Width / 2));
        Canvas.SetTop(WhiteboardResizeThumb, top + (height - WhiteboardResizeThumb.Height) / 2);

        if (updateClip)
        {
            UpdateWhiteboardClip();
        }
    }

    private void MoveWhiteboardContent(double deltaX, double deltaY)
    {
        if (Math.Abs(deltaX) < 0.01 && Math.Abs(deltaY) < 0.01)
        {
            return;
        }

        var matrix = new Matrix(1, 0, 0, 1, deltaX, deltaY);
        if (InkSurface.Strokes.Count > 0)
        {
            InkSurface.Strokes.Transform(matrix, false);
        }

        foreach (var shape in ShapeLayer.Children.OfType<ShapeAnnotationControl>())
        {
            var start = new Point(shape.StartAbsolute.X + deltaX, shape.StartAbsolute.Y + deltaY);
            var end = new Point(shape.EndAbsolute.X + deltaX, shape.EndAbsolute.Y + deltaY);
            shape.SetAbsolutePoints(start, end);
        }

        foreach (var text in TextLayer.Children.OfType<TextAnnotationControl>())
        {
            var left = Canvas.GetLeft(text);
            var top = Canvas.GetTop(text);
            if (double.IsNaN(left))
            {
                left = 0;
            }
            if (double.IsNaN(top))
            {
                top = 0;
            }
            Canvas.SetLeft(text, left + deltaX);
            Canvas.SetTop(text, top + deltaY);
        }
    }

    private void BeginWhiteboardGhost()
    {
        if (WhiteboardPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        EnsureWhiteboardSize();
        var toolbarSize = _toolbarDragSize.Width > 0 ? _toolbarDragSize : GetToolbarSize();
        var toolbarLeft = Canvas.GetLeft(Toolbar);
        var toolbarTop = Canvas.GetTop(Toolbar);
        if (double.IsNaN(toolbarLeft))
        {
            toolbarLeft = 0;
        }
        if (double.IsNaN(toolbarTop))
        {
            toolbarTop = 0;
        }

        _whiteboardGhostLeft = toolbarLeft + toolbarSize.Width + WhiteboardGap;
        _whiteboardGhostTop = toolbarTop;
        WhiteboardGhost.Width = _whiteboardWidth;
        WhiteboardGhost.Height = toolbarSize.Height;
        Canvas.SetLeft(WhiteboardGhost, _whiteboardGhostLeft);
        Canvas.SetTop(WhiteboardGhost, _whiteboardGhostTop);
        WhiteboardGhost.Visibility = Visibility.Visible;
        _whiteboardGhosting = true;
    }

    private void UpdateWhiteboardGhost()
    {
        if (!_whiteboardGhosting)
        {
            return;
        }

        var toolbarSize = _toolbarDragSize.Width > 0 ? _toolbarDragSize : GetToolbarSize();
        var toolbarLeft = Canvas.GetLeft(Toolbar);
        var toolbarTop = Canvas.GetTop(Toolbar);
        if (double.IsNaN(toolbarLeft))
        {
            toolbarLeft = 0;
        }
        if (double.IsNaN(toolbarTop))
        {
            toolbarTop = 0;
        }

        _whiteboardGhostLeft = toolbarLeft + toolbarSize.Width + WhiteboardGap;
        _whiteboardGhostTop = toolbarTop;
        WhiteboardGhost.Width = _whiteboardWidth;
        WhiteboardGhost.Height = toolbarSize.Height;
        Canvas.SetLeft(WhiteboardGhost, _whiteboardGhostLeft);
        Canvas.SetTop(WhiteboardGhost, _whiteboardGhostTop);
    }

    private void CommitWhiteboardGhost()
    {
        if (!_whiteboardGhosting)
        {
            return;
        }

        WhiteboardGhost.Visibility = Visibility.Collapsed;
        _whiteboardGhosting = false;

        var height = WhiteboardGhost.Height > 0 ? WhiteboardGhost.Height : WhiteboardPanel.Height;
        PositionWhiteboardAt(_whiteboardGhostLeft, _whiteboardGhostTop, _whiteboardWidth, height, true);
    }

    private void UpdateWhiteboardClip()
    {
        if (WhiteboardPanel.Visibility != Visibility.Visible)
        {
            if (_whiteboardClipApplied)
            {
                InkSurface.Clip = null;
                ShapeLayer.Clip = null;
                TextLayer.Clip = null;
                _whiteboardClipApplied = false;
            }
            return;
        }

        var left = Canvas.GetLeft(WhiteboardPanel);
        var top = Canvas.GetTop(WhiteboardPanel);
        if (double.IsNaN(left))
        {
            left = 0;
        }
        if (double.IsNaN(top))
        {
            top = 0;
        }

        var width = WhiteboardPanel.ActualWidth > 0 ? WhiteboardPanel.ActualWidth : WhiteboardPanel.Width;
        var height = WhiteboardPanel.ActualHeight > 0 ? WhiteboardPanel.ActualHeight : WhiteboardPanel.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var radius = WhiteboardPanel.CornerRadius.TopLeft;
        _whiteboardClip.Rect = new Rect(left, top, width, height);
        _whiteboardClip.RadiusX = radius;
        _whiteboardClip.RadiusY = radius;

        if (!_whiteboardClipApplied)
        {
            InkSurface.Clip = _whiteboardClip;
            ShapeLayer.Clip = _whiteboardClip;
            TextLayer.Clip = _whiteboardClip;
            _whiteboardClipApplied = true;
        }
    }

    private Button CreateColorPaletteButton(ColorOption option)
    {
        var button = new Button
        {
            Style = (Style)FindResource("ToolButtonStyle"),
            Width = 36,
            Height = 36,
            Margin = new Thickness(0, 4, 0, 4),
            Content = new System.Windows.Shapes.Rectangle
            {
                Width = 16,
                Height = 16,
                RadiusX = 4,
                RadiusY = 4,
                Stroke = new SolidColorBrush(Color.FromArgb(34, 0, 0, 0)),
                Fill = new SolidColorBrush(option.Color)
            }
        };

        button.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _selectedColor = option.Color;
            UpdateColorSwatch();
            UpdateDrawingAttributes();
            UpdateSelectedTextStyle();
            e.Handled = true;
        };

        return button;
    }

    private Button CreateFontPaletteButton(double size)
    {
        var button = new Button
        {
            Style = (Style)FindResource("ToolButtonStyle"),
            Width = 36,
            Height = 36,
            Margin = new Thickness(0, 4, 0, 4),
            Content = new TextBlock
            {
                Text = size.ToString("0"),
                Foreground = Brushes.White,
                FontSize = 12,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            }
        };

        button.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _selectedFontSize = size;
            UpdateFontSizeLabel();
            UpdateSelectedTextStyle();
            e.Handled = true;
        };

        return button;
    }

    private void UpdateColorSwatch()
    {
        ColorSwatch.Fill = new SolidColorBrush(_selectedColor);
    }

    private void UpdateFontSizeLabel()
    {
        FontSizeLabel.Text = _selectedFontSize.ToString("0");
    }

    private void UpdateInkToolIcon(ToolMode mode)
    {
        var isHighlighter = mode == ToolMode.Highlighter;
        InkIconPen.Visibility = isHighlighter ? Visibility.Collapsed : Visibility.Visible;
        InkIconHighlighter.Visibility = isHighlighter ? Visibility.Visible : Visibility.Collapsed;
        PenInkButton.IsChecked = !isHighlighter;
        HighlighterInkButton.IsChecked = isHighlighter;
    }

    private bool IsWithinShapePalette(DependencyObject? source, Point positionInUiLayer)
    {
        if (ShapePalette.Visibility != Visibility.Visible)
        {
            return false;
        }

        if (ShapePalette.IsMouseOver)
        {
            return true;
        }

        var left = Canvas.GetLeft(ShapePalette);
        var top = Canvas.GetTop(ShapePalette);
        if (double.IsNaN(left))
        {
            left = 0;
        }
        if (double.IsNaN(top))
        {
            top = 0;
        }

        var bounds = new Rect(left, top, ShapePalette.ActualWidth, ShapePalette.ActualHeight);
        if (bounds.Contains(positionInUiLayer))
        {
            return true;
        }

        var current = source;
        while (current != null)
        {
            if (ReferenceEquals(current, ShapePalette))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private bool IsWithinInkPalette(DependencyObject? source, Point positionInUiLayer)
    {
        if (InkPalette.Visibility != Visibility.Visible)
        {
            return false;
        }

        if (InkPalette.IsMouseOver)
        {
            return true;
        }

        var left = Canvas.GetLeft(InkPalette);
        var top = Canvas.GetTop(InkPalette);
        if (double.IsNaN(left))
        {
            left = 0;
        }
        if (double.IsNaN(top))
        {
            top = 0;
        }

        var bounds = new Rect(left, top, InkPalette.ActualWidth, InkPalette.ActualHeight);
        if (bounds.Contains(positionInUiLayer))
        {
            return true;
        }

        var current = source;
        while (current != null)
        {
            if (ReferenceEquals(current, InkPalette))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private bool IsWithinColorPalette(DependencyObject? source, Point positionInUiLayer)
    {
        if (ColorPalette.Visibility != Visibility.Visible)
        {
            return false;
        }

        if (ColorPalette.IsMouseOver)
        {
            return true;
        }

        var left = Canvas.GetLeft(ColorPalette);
        var top = Canvas.GetTop(ColorPalette);
        if (double.IsNaN(left))
        {
            left = 0;
        }
        if (double.IsNaN(top))
        {
            top = 0;
        }

        var bounds = new Rect(left, top, ColorPalette.ActualWidth, ColorPalette.ActualHeight);
        if (bounds.Contains(positionInUiLayer))
        {
            return true;
        }

        var current = source;
        while (current != null)
        {
            if (ReferenceEquals(current, ColorPalette))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private bool IsWithinFontPalette(DependencyObject? source, Point positionInUiLayer)
    {
        if (FontPalette.Visibility != Visibility.Visible)
        {
            return false;
        }

        if (FontPalette.IsMouseOver)
        {
            return true;
        }

        var left = Canvas.GetLeft(FontPalette);
        var top = Canvas.GetTop(FontPalette);
        if (double.IsNaN(left))
        {
            left = 0;
        }
        if (double.IsNaN(top))
        {
            top = 0;
        }

        var bounds = new Rect(left, top, FontPalette.ActualWidth, FontPalette.ActualHeight);
        if (bounds.Contains(positionInUiLayer))
        {
            return true;
        }

        var current = source;
        while (current != null)
        {
            if (ReferenceEquals(current, FontPalette))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private bool IsWithinSettingsPanel(DependencyObject? source, Point positionInUiLayer)
    {
        if (SettingsPanel.Visibility != Visibility.Visible)
        {
            return false;
        }

        if (SettingsPanel.IsMouseOver)
        {
            return true;
        }

        var left = Canvas.GetLeft(SettingsPanel);
        var top = Canvas.GetTop(SettingsPanel);
        if (double.IsNaN(left))
        {
            left = 0;
        }
        if (double.IsNaN(top))
        {
            top = 0;
        }

        var bounds = new Rect(left, top, SettingsPanel.ActualWidth, SettingsPanel.ActualHeight);
        if (bounds.Contains(positionInUiLayer))
        {
            return true;
        }

        var current = source;
        while (current != null)
        {
            if (ReferenceEquals(current, SettingsPanel))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private bool IsWithinWhiteboard(Point positionInUiLayer)
    {
        if (WhiteboardPanel.Visibility != Visibility.Visible)
        {
            return false;
        }

        var left = Canvas.GetLeft(WhiteboardPanel);
        var top = Canvas.GetTop(WhiteboardPanel);
        if (double.IsNaN(left))
        {
            left = 0;
        }
        if (double.IsNaN(top))
        {
            top = 0;
        }

        var width = WhiteboardPanel.ActualWidth > 0 ? WhiteboardPanel.ActualWidth : WhiteboardPanel.Width;
        var height = WhiteboardPanel.ActualHeight > 0 ? WhiteboardPanel.ActualHeight : WhiteboardPanel.Height;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        var bounds = new Rect(left, top, width, height);
        return bounds.Contains(positionInUiLayer);
    }

    private bool ShouldRestrictToWhiteboard(DependencyObject? source, Point positionInUiLayer)
    {
        if (WhiteboardPanel.Visibility != Visibility.Visible || _toolMode == ToolMode.Cursor)
        {
            return false;
        }

        if (IsWithinWhiteboard(positionInUiLayer))
        {
            return false;
        }

        return !IsWithinToolbar(source, positionInUiLayer)
               && !IsWithinShapePalette(source, positionInUiLayer)
               && !IsWithinInkPalette(source, positionInUiLayer)
               && !IsWithinColorPalette(source, positionInUiLayer)
               && !IsWithinFontPalette(source, positionInUiLayer)
               && !IsWithinSettingsPanel(source, positionInUiLayer);
    }

    private void SelectText(TextAnnotationControl control)
    {
        if (_selectedText == control)
        {
            return;
        }

        DeselectShape();
        DeselectText();
        _selectedText = control;
        _selectedText.SetSelected(true);
        _selectedColor = ((SolidColorBrush)_selectedText.TextBrush).Color;
        _selectedFontSize = _selectedText.FontSizeValue;
        UpdateColorSwatch();
        UpdateFontSizeLabel();
        TextBackgroundToggle.IsChecked = _selectedText.HasBackground;
    }

    private bool IsWithinToolbar(DependencyObject? source)
    {
        return IsWithinToolbar(source, Mouse.GetPosition(UiLayer));
    }

    private bool IsWithinToolbar(DependencyObject? source, Point positionInUiLayer)
    {
        if (Toolbar.IsMouseOver)
        {
            return true;
        }

        var left = Canvas.GetLeft(Toolbar);
        var top = Canvas.GetTop(Toolbar);
        if (double.IsNaN(left))
        {
            left = 0;
        }
        if (double.IsNaN(top))
        {
            top = 0;
        }

        var bounds = new Rect(left, top, Toolbar.ActualWidth, Toolbar.ActualHeight);
        if (bounds.Contains(positionInUiLayer))
        {
            return true;
        }

        var current = source;
        while (current != null)
        {
            if (ReferenceEquals(current, Toolbar))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void SetCursorForTool(ToolMode mode)
    {
        var cursor = Cursors.Arrow;

        switch (mode)
        {
            case ToolMode.Pen:
            case ToolMode.Highlighter:
                cursor = Cursors.Pen;
                break;
            case ToolMode.Eraser:
                cursor = Cursors.Cross;
                break;
            case ToolMode.Text:
                cursor = Cursors.IBeam;
                break;
            case ToolMode.Shapes:
                cursor = Cursors.Cross;
                break;
            case ToolMode.Cursor:
                cursor = Cursors.Arrow;
                break;
            default:
                cursor = Cursors.Arrow;
                break;
        }

        Cursor = cursor;
        InkSurface.UseCustomCursor = true;
        InkSurface.ForceCursor = true;
        InkSurface.Cursor = cursor;
    }

    private void SetActiveToolButton(ToolMode mode)
    {
        ClearToolButtonHighlights();

        var button = mode switch
        {
            ToolMode.Cursor => CursorButton,
            ToolMode.Pen => PenButton,
            ToolMode.Highlighter => PenButton,
            ToolMode.Eraser => EraserButton,
            ToolMode.Text => TextButton,
            ToolMode.Shapes => ShapeButton,
            _ => null
        };

        if (button == null)
        {
            return;
        }

        button.Background = _activeToolBrush;
        button.Foreground = Brushes.White;
    }

    private void ClearToolButtonHighlights()
    {
        ResetToolButton(CursorButton);
        ResetToolButton(PenButton);
        ResetToolButton(HighlighterButton);
        ResetToolButton(EraserButton);
        ResetToolButton(TextButton);
        ResetToolButton(ShapeButton);
    }

    private static void ResetToolButton(Button button)
    {
        button.ClearValue(BackgroundProperty);
        button.ClearValue(ForegroundProperty);
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        var current = source;
        while (current != null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void DeselectText()
    {
        if (_selectedText == null)
        {
            return;
        }

        _selectedText.SetSelected(false);
        _selectedText.EndEdit();
        _selectedText = null;
    }

    private bool TryEraseText(Point position)
    {
        for (var i = TextLayer.Children.Count - 1; i >= 0; i--)
        {
            if (TextLayer.Children[i] is not TextAnnotationControl text)
            {
                continue;
            }

            if (!IsPointWithinControl(text, position))
            {
                continue;
            }

            if (ReferenceEquals(_selectedText, text))
            {
                DeselectText();
            }

            TextLayer.Children.RemoveAt(i);
            return true;
        }

        return false;
    }

    private bool TryEraseShape(Point position)
    {
        for (var i = ShapeLayer.Children.Count - 1; i >= 0; i--)
        {
            if (ShapeLayer.Children[i] is not ShapeAnnotationControl shape)
            {
                continue;
            }

            if (!IsPointWithinControl(shape, position))
            {
                continue;
            }

            if (ReferenceEquals(_selectedShape, shape))
            {
                DeselectShape();
            }

            ShapeLayer.Children.RemoveAt(i);
            return true;
        }

        return false;
    }

    private static bool IsPointWithinControl(UIElement control, Point positionInLayer)
    {
        var left = Canvas.GetLeft(control);
        if (double.IsNaN(left))
        {
            left = 0;
        }

        var top = Canvas.GetTop(control);
        if (double.IsNaN(top))
        {
            top = 0;
        }

        var width = 0.0;
        var height = 0.0;
        if (control is FrameworkElement element)
        {
            width = element.ActualWidth > 0 ? element.ActualWidth : element.Width;
            height = element.ActualHeight > 0 ? element.ActualHeight : element.Height;
        }

        if (width <= 0 || height <= 0)
        {
            return false;
        }

        var bounds = new Rect(left, top, width, height);
        return bounds.Contains(positionInLayer);
    }

    private void UpdateSelectedTextStyle()
    {
        if (_selectedText == null)
        {
            return;
        }

        _selectedText.TextBrush = new SolidColorBrush(_selectedColor);
        _selectedText.FontSizeValue = _selectedFontSize;
        _selectedText.HasBackground = TextBackgroundToggle.IsChecked == true;
        ScheduleSave();
    }

    private void ToggleCursorMode()
    {
        if (_toolMode == ToolMode.Cursor)
        {
            SetTool(_lastNonCursorTool);
        }
        else
        {
            SetTool(ToolMode.Cursor);
        }
    }

    private void ToggleVisibility()
    {
        var target = Visibility == Visibility.Visible ? Visibility.Hidden : Visibility.Visible;
        Visibility = target;
        if (_toolbarWindow != null)
        {
            _toolbarWindow.Visibility = target;
        }
    }

    private void ToggleToolbar()
    {
        _toolbarCollapsed = !_toolbarCollapsed;
        ToolbarToggleIcon.Text = _toolbarCollapsed ? "\uE710" : "\uE711";
        ToolbarToggleButton.ToolTip = _toolbarCollapsed ? "Expand" : "Collapse";

        var content = ToolbarContent;
        var scale = (ScaleTransform)content.RenderTransform;
        var duration = TimeSpan.FromMilliseconds(160);
        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };

        if (_toolbarCollapsed)
        {
            CloseAllPalettes();
            SetSettingsVisible(false);
            var scaleAnim = new DoubleAnimation(0, duration) { EasingFunction = easing };
            var opacityAnim = new DoubleAnimation(0, duration) { EasingFunction = easing };
            opacityAnim.Completed += (_, _) => content.Visibility = Visibility.Collapsed;
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
            content.BeginAnimation(OpacityProperty, opacityAnim);
            PositionWhiteboard();
            return;
        }

        content.Visibility = Visibility.Visible;
        var showScale = new DoubleAnimation(1, duration) { EasingFunction = easing };
        var showOpacity = new DoubleAnimation(1, duration) { EasingFunction = easing };
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, showScale);
        content.BeginAnimation(OpacityProperty, showOpacity);
        PositionWhiteboard();
    }

    private void CloseAllPalettes()
    {
        ShapePalette.Visibility = Visibility.Collapsed;
        InkPalette.Visibility = Visibility.Collapsed;
        ColorPalette.Visibility = Visibility.Collapsed;
        FontPalette.Visibility = Visibility.Collapsed;
    }

    // Screenshot functionality removed.

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_NCHITTEST)
        {
            handled = true;
            return _clickThroughEnabled ? new IntPtr(Win32.HTTRANSPARENT) : new IntPtr(Win32.HTCLIENT);
        }

        return IntPtr.Zero;
    }

    private void SetClickThrough(bool enabled)
    {
        _clickThroughEnabled = enabled;
        var handle = new WindowInteropHelper(this).Handle;
        var styles = Win32.GetWindowLongPtr(handle, Win32.GWL_EXSTYLE).ToInt64();
        var hasFlag = (styles & Win32.WS_EX_TRANSPARENT) != 0;
        if (enabled == hasFlag)
        {
            return;
        }

        if (enabled)
        {
            styles |= Win32.WS_EX_TRANSPARENT;
        }
        else
        {
            styles &= ~Win32.WS_EX_TRANSPARENT;
        }

        Win32.SetWindowLongPtr(handle, Win32.GWL_EXSTYLE, new IntPtr(styles));
        Win32.SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_FRAMECHANGED);
    }

    private void ClearAll()
    {
        InkSurface.Strokes.Clear();
        ShapeLayer.Children.Clear();
        TextLayer.Children.Clear();
        _undoManager.Clear();
        _undoManager.Push(BuildSession());
        ScheduleSave();
    }

    private void Undo()
    {
        var session = _undoManager.Undo();
        if (session == null)
        {
            return;
        }

        RestoreSession(session);
        ScheduleSave();
    }

    private void ScheduleSave()
    {
        if (_isRestoring)
        {
            return;
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveSession()
    {
        _saveTimer.Stop();
        if (_isRestoring)
        {
            return;
        }

        var session = BuildSession();
        _sessionStore.Save(session);
        _undoManager.Push(session);
    }

    private AnnotationSession BuildSession()
    {
        var session = new AnnotationSession();

        foreach (var stroke in InkSurface.Strokes)
        {
            var data = new StrokeData
            {
                Color = stroke.DrawingAttributes.Color.ToString(),
                Thickness = stroke.DrawingAttributes.Width,
                Opacity = stroke.DrawingAttributes.Color.A / 255.0,
                IsHighlighter = stroke.DrawingAttributes.IsHighlighter
            };

            foreach (var point in stroke.StylusPoints)
            {
                data.Points.Add(new PointData { X = point.X, Y = point.Y });
            }

            session.Strokes.Add(data);
        }

        foreach (var child in TextLayer.Children.OfType<TextAnnotationControl>())
        {
            var data = new TextBoxData
            {
                X = Canvas.GetLeft(child),
                Y = Canvas.GetTop(child),
                Width = child.ActualWidth,
                Height = child.ActualHeight,
                Text = child.Text,
                FontSize = child.FontSizeValue,
                TextColor = ((SolidColorBrush)child.TextBrush).Color.ToString(),
                HasBackground = child.HasBackground
            };
            session.TextBoxes.Add(data);
        }

        foreach (var shape in ShapeLayer.Children.OfType<ShapeAnnotationControl>())
        {
            var data = new ShapeData
            {
                Type = shape.ShapeType,
                StartX = shape.StartAbsolute.X,
                StartY = shape.StartAbsolute.Y,
                EndX = shape.EndAbsolute.X,
                EndY = shape.EndAbsolute.Y,
                Thickness = shape.StrokeThickness,
                StrokeColor = (shape.Stroke as SolidColorBrush)?.Color.ToString() ?? Colors.White.ToString()
            };
            session.Shapes.Add(data);
        }

        return session;
    }

    private void LoadSession()
    {
        var session = _sessionStore.Load();
        if (session == null)
        {
            return;
        }

        RestoreSession(session);
    }

    private void RestoreSession(AnnotationSession session)
    {
        _isRestoring = true;
        InkSurface.Strokes.Clear();
        ShapeLayer.Children.Clear();
        TextLayer.Children.Clear();
        DeselectText();
        DeselectShape();

        foreach (var strokeData in session.Strokes)
        {
            var points = new StylusPointCollection(strokeData.Points.Select(p => new StylusPoint(p.X, p.Y)));
            var stroke = new Stroke(points)
            {
                DrawingAttributes = new DrawingAttributes
                {
                    Color = (Color)ColorConverter.ConvertFromString(strokeData.Color)!,
                    Width = strokeData.Thickness,
                    Height = strokeData.Thickness,
                    IsHighlighter = strokeData.IsHighlighter,
                    FitToCurve = true
                }
            };
            InkSurface.Strokes.Add(stroke);
        }

        foreach (var textData in session.TextBoxes)
        {
            var control = CreateTextControl();
            control.Text = textData.Text;
            control.FontSizeValue = textData.FontSize;
            control.TextBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(textData.TextColor)!);
            control.HasBackground = textData.HasBackground;
            control.Width = textData.Width;
            control.Height = textData.Height;
            Canvas.SetLeft(control, textData.X);
            Canvas.SetTop(control, textData.Y);
            TextLayer.Children.Add(control);
        }

        foreach (var shapeData in session.Shapes)
        {
            var control = CreateShapeControl();
            control.ShapeType = shapeData.Type;
            control.Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(shapeData.StrokeColor)!);
            control.StrokeThickness = shapeData.Thickness;
            control.SetAbsolutePoints(new Point(shapeData.StartX, shapeData.StartY),
                new Point(shapeData.EndX, shapeData.EndY));
            ShapeLayer.Children.Add(control);
        }

        _isRestoring = false;
    }

    private sealed record ColorOption(string Name, Color Color)
    {
        public override string ToString() => Name;
        public SolidColorBrush Brush => new(Color);
    }


    private enum ToolMode
    {
        Cursor,
        Pen,
        Highlighter,
        Eraser,
        Text,
        Shapes
    }

    private enum HotkeyAction
    {
        ToggleCursor,
        ToggleVisibility,
        CloseApp,
        ClearAll,
        Undo,
        SelectPen,
        SelectHighlighter,
        SelectEraser,
        ShapeLine,
        ShapeArrow,
        ShapeRectangle,
        ShapeEllipse,
        ToggleColorPalette
    }
}
