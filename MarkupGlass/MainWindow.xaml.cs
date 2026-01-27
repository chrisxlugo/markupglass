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
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
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
    private readonly Brush _activeToolBrush = SystemColors.HighlightBrush;
    private Color _selectedColor = Colors.White;
    private double _selectedFontSize = 16;
    private ToolMode _toolMode = ToolMode.Pen;
    private ToolMode _lastNonCursorTool = ToolMode.Pen;
    private bool _toolbarCollapsed;
    private bool _isSnipping;
    private bool _isRestoring;
    private Point _toolbarDragStart;
    private Point _toolbarOrigin;
    private bool _toolbarDragging;
    private TextAnnotationControl? _selectedText;
    private ShapeAnnotationControl? _selectedShape;
    private string? _screenshotFolder;
    private HwndSource? _source;
    private ToolbarWindow? _toolbarWindow;
    private Point _snipStart;
    private bool _isPlacingShape;
    private Point _shapeStart;
    private ShapeAnnotationControl? _activeShape;
    private ShapeType _shapeType = ShapeType.Rectangle;
    private HotkeySettings _hotkeySettings = new();
    private bool _settingsDragging;
    private Point _settingsDragStart;
    private Point _settingsOrigin;
    private bool _moveTextMode;
    private bool _suppressTextCreateOnClick;
    private bool _moveTextChordHeld;
    private bool _clickThroughEnabled;

    public MainWindow()
    {
        InitializeComponent();
        _hotkeySettings = _settingsStore.Load();
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
            if (_toolbarWindow != null)
            {
                _toolbarWindow.Visibility = Visibility.Hidden;
            }
            return;
        }

        if (_toolbarWindow != null && Visibility == Visibility.Visible)
        {
            _toolbarWindow.Visibility = Visibility.Visible;
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
            _settingsStore.Save(_hotkeySettings);
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
                FontPaletteLayer
            };

            foreach (var layer in layers)
            {
                root.Children.Remove(layer);
            }

            _toolbarWindow.AttachLayers(layers);
            _toolbarWindow.SetInteractiveElements(new[]
            {
                Toolbar,
                ShapePalette,
                InkPalette,
                ColorPalette,
                FontPalette,
                SettingsPanel
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
            Toolbar.CaptureMouse();
        };

        Toolbar.MouseMove += (_, e) =>
        {
            if (!_toolbarDragging)
            {
                return;
            }

            var current = e.GetPosition(UiLayer);
            var offset = current - _toolbarDragStart;
            Canvas.SetLeft(Toolbar, _toolbarOrigin.X + offset.X);
            Canvas.SetTop(Toolbar, _toolbarOrigin.Y + offset.Y);
            PositionPalettes();
        };

        Toolbar.MouseLeftButtonUp += (_, _) =>
        {
            if (!_toolbarDragging)
            {
                return;
            }

            _toolbarDragging = false;
            Toolbar.ReleaseMouseCapture();
            PositionPalettes();
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
        SettingsButton.Click += (_, _) => ToggleSettings();
        CloseSettingsButton.Click += (_, _) => SetSettingsVisible(false);
        CloseSettingsHeaderButton.Click += (_, _) => SetSettingsVisible(false);
        ApplyHotkeysButton.Click += (_, _) => ApplyHotkeysAndSave();
        CameraButton.Click += (_, _) => StartSnip();
        UndoButton.Click += (_, _) => Undo();
        ClearButton.Click += (_, _) => ClearAll();
        TextBackgroundToggle.Checked += (_, _) => UpdateSelectedTextStyle();
        TextBackgroundToggle.Unchecked += (_, _) => UpdateSelectedTextStyle();

        LineShapeButton.PreviewMouseLeftButtonDown += (s, e) => HandleShapePaletteClick(ShapeType.Line, e);
        ArrowShapeButton.PreviewMouseLeftButtonDown += (s, e) => HandleShapePaletteClick(ShapeType.Arrow, e);
        RectShapeButton.PreviewMouseLeftButtonDown += (s, e) => HandleShapePaletteClick(ShapeType.Rectangle, e);
        EllipseShapeButton.PreviewMouseLeftButtonDown += (s, e) => HandleShapePaletteClick(ShapeType.Ellipse, e);

        PenInkButton.PreviewMouseLeftButtonDown += (s, e) => HandleInkPaletteClick(ToolMode.Pen, e);
        HighlighterInkButton.PreviewMouseLeftButtonDown += (s, e) => HandleInkPaletteClick(ToolMode.Highlighter, e);
    }

    private void SetupPickers()
    {
        _colors.Add(new ColorOption("White", Colors.White));
        _colors.Add(new ColorOption("Red", Colors.Red));
        _colors.Add(new ColorOption("Yellow", Colors.Yellow));
        _colors.Add(new ColorOption("Aqua", Colors.Aqua));
        _colors.Add(new ColorOption("Lime", Colors.Lime));
        _selectedColor = _colors[0].Color;
        UpdateColorSwatch();
        foreach (var option in _colors)
        {
            ColorPalettePanel.Children.Add(CreateColorPaletteButton(option));
        }

        var fontSizes = new List<double> { 12, 16, 20, 24, 32 };
        _selectedFontSize = fontSizes[1];
        UpdateFontSizeLabel();
        foreach (var size in fontSizes)
        {
            FontPalettePanel.Children.Add(CreateFontPaletteButton(size));
        }

        ThicknessSlider.ValueChanged += (_, _) => UpdateDrawingAttributes();
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

        if (_isSnipping && e.LeftButton == MouseButtonState.Pressed)
        {
            _snipStart = e.GetPosition(this);
            UpdateSnipSelection(_snipStart, _snipStart);
            SnipSelection.Visibility = Visibility.Visible;
            SnipLayer.Visibility = Visibility.Visible;
            CaptureMouse();
            e.Handled = true;
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

        if (!_isSnipping || !IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var current = e.GetPosition(this);
        UpdateSnipSelection(_snipStart, current);
        e.Handled = true;
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

        if (!_isSnipping || !IsMouseCaptured || e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        ReleaseMouseCapture();
        var end = e.GetPosition(this);
        var rect = new Rect(_snipStart, end);
        await FinishSnipAsync(rect);
        e.Handled = true;
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
            return;
        }

        content.Visibility = Visibility.Visible;
        var showScale = new DoubleAnimation(1, duration) { EasingFunction = easing };
        var showOpacity = new DoubleAnimation(1, duration) { EasingFunction = easing };
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, showScale);
        content.BeginAnimation(OpacityProperty, showOpacity);
    }

    private void CloseAllPalettes()
    {
        ShapePalette.Visibility = Visibility.Collapsed;
        InkPalette.Visibility = Visibility.Collapsed;
        ColorPalette.Visibility = Visibility.Collapsed;
        FontPalette.Visibility = Visibility.Collapsed;
    }

    private void StartSnip()
    {
        if (!EnsureScreenshotFolder())
        {
            return;
        }

        _isSnipping = true;
        SnipLayer.Visibility = Visibility.Visible;
        SnipSelection.Visibility = Visibility.Visible;
        SnipSelection.Width = 0;
        SnipSelection.Height = 0;
        Cursor = Cursors.Cross;
    }

    private void UpdateSnipSelection(Point start, Point end)
    {
        var rect = new Rect(start, end);
        var left = Math.Min(rect.Left, rect.Right);
        var top = Math.Min(rect.Top, rect.Bottom);
        var width = Math.Abs(rect.Width);
        var height = Math.Abs(rect.Height);

        Canvas.SetLeft(SnipSelection, left);
        Canvas.SetTop(SnipSelection, top);
        SnipSelection.Width = width;
        SnipSelection.Height = height;
    }

    private async Task FinishSnipAsync(Rect rect)
    {
        SnipSelection.Visibility = Visibility.Collapsed;
        SnipLayer.Visibility = Visibility.Collapsed;

        if (rect.Width < 2 || rect.Height < 2)
        {
            _isSnipping = false;
            SetCursorForTool(_toolMode);
            return;
        }

        var previousOpacity = Opacity;
        Opacity = 0;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(40);

        try
        {
            SaveSnip(rect);
        }
        finally
        {
            Opacity = previousOpacity;
            _isSnipping = false;
            SetCursorForTool(_toolMode);
        }
    }

    private void SaveSnip(Rect rect)
    {
        if (string.IsNullOrWhiteSpace(_screenshotFolder))
        {
            return;
        }

        var topLeft = PointToScreen(new Point(rect.Left, rect.Top));
        var bottomRight = PointToScreen(new Point(rect.Right, rect.Bottom));
        var x = (int)Math.Round(Math.Min(topLeft.X, bottomRight.X));
        var y = (int)Math.Round(Math.Min(topLeft.Y, bottomRight.Y));
        var width = (int)Math.Round(Math.Abs(bottomRight.X - topLeft.X));
        var height = (int)Math.Round(Math.Abs(bottomRight.Y - topLeft.Y));

        if (width <= 0 || height <= 0)
        {
            return;
        }

        Directory.CreateDirectory(_screenshotFolder);
        var fileName = $"EpicPen_{DateTime.Now:yyyyMMdd_HHmmss}.png";
        var path = Path.Combine(_screenshotFolder, fileName);

        using var bmp = new Drawing.Bitmap(width, height, DrawingImaging.PixelFormat.Format32bppArgb);
        using var graphics = Drawing.Graphics.FromImage(bmp);
        graphics.CopyFromScreen(x, y, 0, 0, new Drawing.Size(width, height), Drawing.CopyPixelOperation.SourceCopy);
        bmp.Save(path, DrawingImaging.ImageFormat.Png);
    }

    private bool EnsureScreenshotFolder()
    {
        if (!string.IsNullOrWhiteSpace(_screenshotFolder) && Directory.Exists(_screenshotFolder))
        {
            return true;
        }

        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Choose a folder for screenshots",
            UseDescriptionForTitle = true
        };

        var result = dialog.ShowDialog();
        if (result != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            return false;
        }

        _screenshotFolder = dialog.SelectedPath;
        return true;
    }

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
