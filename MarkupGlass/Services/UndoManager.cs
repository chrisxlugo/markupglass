using System.Collections.Generic;
using MarkupGlass.Models;

namespace MarkupGlass.Services;

internal sealed class UndoManager
{
    private readonly Stack<string> _states = new();
    private readonly SessionStore _store;
    private readonly int _maxStates;

    public UndoManager(SessionStore store, int maxStates = 50)
    {
        _store = store;
        _maxStates = maxStates;
    }

    public void Push(AnnotationSession session)
    {
        _states.Push(_store.Serialize(session));
        Trim();
    }

    public AnnotationSession? Undo()
    {
        if (_states.Count <= 1)
        {
            return null;
        }

        _states.Pop();
        var json = _states.Peek();
        return _store.Deserialize(json);
    }

    public void Clear()
    {
        _states.Clear();
    }

    private void Trim()
    {
        if (_states.Count <= _maxStates)
        {
            return;
        }

        var items = _states.ToArray();
        _states.Clear();
        for (var i = items.Length - 1; i >= 0; i--)
        {
            _states.Push(items[i]);
            if (_states.Count >= _maxStates)
            {
                break;
            }
        }
    }
}
