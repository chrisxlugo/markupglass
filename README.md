# MarkupGlass

Always-on-top transparent overlay for drawing and text annotations on Windows 10/11.

## What this code does
MarkupGlass is a WPF desktop app that creates a full-screen transparent window on top of all monitors.
It lets you draw ink strokes, add movable/resizable text boxes, and take snips while keeping the desktop visible.

## Build and run
Requirements:
- Windows 10/11
- .NET 8 SDK and/or Visual Studio 2022 with the .NET desktop workload

Option A: Visual Studio
1) Open `MarkupGlass.sln` in Visual Studio 2022.
2) Build and run the `MarkupGlass` project (net8.0-windows).

Option B: CLI
1) `dotnet build MarkupGlass\MarkupGlass.csproj`
2) `dotnet run --project MarkupGlass\MarkupGlass.csproj`

## App icon and shortcuts
- The app icon is baked into the executable via `MarkupGlass/App.ico`.
- After building, run `CreateShortcuts.ps1` to create Desktop and Start Menu shortcuts.
  - `.\CreateShortcuts.ps1 -Configuration Debug`
  - `.\CreateShortcuts.ps1 -Configuration Release`

## How to use the app
- Use the toolbar to select tools (pen, highlighter, eraser, text, cursor, screenshot).
- Draw directly on the screen in pen/highlighter modes.
- Use the text tool to place editable text boxes.
- Use the cursor tool (F8) to make the overlay click-through.
- Use Undo/Clear to remove annotations.
- Use the screenshot tool to capture a rectangular area and save it.

## Hotkeys
- F8: Toggle draw vs pass-through.
- F9: Show/hide overlay.
- F10: Quit app.
- Ctrl+Shift+C: Clear all annotations.
- Ctrl+Z: Undo last annotation change.

## Pass-through behavior
Pass-through mode adds `WS_EX_TRANSPARENT` to the window styles so mouse input falls through to underlying apps. Use F8 to toggle back to draw mode when the overlay is click-through.

## Code map (where things live)
- `MarkupGlass/MainWindow.xaml`: Toolbar UI layout and styling.
- `MarkupGlass/MainWindow.xaml.cs`: Core behavior (tool switching, input handling, undo, snips).
- `MarkupGlass/Controls/TextAnnotationControl.*`: Custom text box control (edit, drag, resize).
- `MarkupGlass/Services/SessionStore.cs`: Load/save annotations to disk.
- `MarkupGlass/Services/UndoManager.cs`: Undo stack for session snapshots.
- `MarkupGlass/Services/HotkeyManager.cs`: Global hotkeys via Win32 hooks.
- `MarkupGlass/Utilities/Win32.cs`: Native interop constants and P/Invoke helpers.
- `MarkupGlass/Models/*.cs`: Data models for strokes and text boxes.

## How the app works (high level)
- UI layers:
  - `InkSurface` (InkCanvas) stores pen/highlighter strokes.
  - `TextLayer` (Canvas) hosts text annotation controls.
  - `UiLayer` (Canvas) hosts the toolbar and snip overlay.
- Tool modes:
  - `ToolMode` toggles InkCanvas editing mode and hit testing.
  - Cursor mode disables hit testing and sets click-through on the window.
- Persistence:
  - Each save builds an `AnnotationSession` snapshot of strokes + text.
  - Sessions are serialized to `%AppData%\MarkupGlass\last-session.json`.
- Undo:
  - After each save, a snapshot is pushed to `UndoManager`.
  - Ctrl+Z restores the previous snapshot.
- Snipping:
  - A temporary selection rectangle is shown on drag.
  - The selected screen area is captured using `System.Drawing` and saved to PNG.

## Persistence format
Annotations are saved to `%AppData%\MarkupGlass\last-session.json`.

Stored data includes:
- Strokes: point list, color (ARGB), thickness, opacity, and highlighter flag.
- Text boxes: x/y position, width/height, text, font size, text color, and background toggle.

## Multi-monitor support
The overlay window currently uses `SystemParameters.VirtualScreen*` so it spans all monitors. For per-monitor persistence, split annotations into multiple sessions keyed by monitor bounds and load/save per monitor.
