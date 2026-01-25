using System;
using System.Collections.Generic;
using System.Windows.Interop;
using MarkupGlass.Utilities;

namespace MarkupGlass.Services;

internal sealed class HotkeyManager : IDisposable
{
    private readonly Dictionary<int, Action> _handlers = new();
    private HwndSource? _source;
    private int _currentId;

    public void Initialize(HwndSource source)
    {
        _source = source;
        _source.AddHook(WndProc);
    }

    public int Register(uint modifiers, uint key, Action handler)
    {
        if (_source == null)
        {
            throw new InvalidOperationException("Hotkey manager not initialized.");
        }

        _currentId++;
        var id = _currentId;
        var result = Win32.RegisterHotKey(_source.Handle, id, modifiers, key);
        if (result == 0)
        {
            throw new InvalidOperationException("Failed to register hotkey.");
        }

        _handlers[id] = handler;
        return id;
    }

    public void Dispose()
    {
        if (_source == null)
        {
            return;
        }

        foreach (var id in _handlers.Keys)
        {
            Win32.UnregisterHotKey(_source.Handle, id);
        }

        _handlers.Clear();
        _source.RemoveHook(WndProc);
        _source = null;
    }

    public void Clear()
    {
        if (_source == null)
        {
            return;
        }

        foreach (var id in _handlers.Keys)
        {
            Win32.UnregisterHotKey(_source.Handle, id);
        }

        _handlers.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY)
        {
            var id = wParam.ToInt32();
            if (_handlers.TryGetValue(id, out var handler))
            {
                handler();
                handled = true;
            }
        }

        return IntPtr.Zero;
    }
}
