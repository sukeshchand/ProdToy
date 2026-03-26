namespace DevToy;

interface IEditorAction
{
    void Execute();
    void Undo();
    string Description { get; }
}

class UndoRedoManager
{
    private readonly Stack<IEditorAction> _undoStack = new();
    private readonly Stack<IEditorAction> _redoStack = new();
    private readonly int _maxHistory;

    public UndoRedoManager(int maxHistory = 30)
    {
        _maxHistory = maxHistory;
    }

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    /// <summary>Access undo stack for serialization (bottom to top order).</summary>
    public IReadOnlyList<IEditorAction> UndoItems => _undoStack.Reverse().ToList();

    /// <summary>Access redo stack for serialization (bottom to top order).</summary>
    public IReadOnlyList<IEditorAction> RedoItems => _redoStack.Reverse().ToList();

    /// <summary>Push directly to undo stack without executing (for restore).</summary>
    public void PushUndoRaw(IEditorAction action) => _undoStack.Push(action);

    /// <summary>Push directly to redo stack without executing (for restore).</summary>
    public void PushRedoRaw(IEditorAction action) => _redoStack.Push(action);

    public event Action? StateChanged;

    public void Execute(IEditorAction action)
    {
        action.Execute();
        _undoStack.Push(action);
        _redoStack.Clear();

        // Trim oldest if over capacity
        if (_undoStack.Count > _maxHistory)
        {
            var temp = new Stack<IEditorAction>(_undoStack.Reverse().Skip(1));
            _undoStack.Clear();
            foreach (var a in temp.Reverse()) _undoStack.Push(a);
        }

        StateChanged?.Invoke();
    }

    public void Undo()
    {
        if (!CanUndo) return;
        var action = _undoStack.Pop();
        action.Undo();
        _redoStack.Push(action);
        StateChanged?.Invoke();
    }

    public void Redo()
    {
        if (!CanRedo) return;
        var action = _redoStack.Pop();
        action.Execute();
        _undoStack.Push(action);
        StateChanged?.Invoke();
    }

    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        StateChanged?.Invoke();
    }
}

// --- Concrete actions ---

class AddObjectAction : IEditorAction
{
    private readonly List<AnnotationObject> _objects;
    private readonly AnnotationObject _obj;
    public string Description => "Add annotation";

    public AddObjectAction(List<AnnotationObject> objects, AnnotationObject obj)
    {
        _objects = objects;
        _obj = obj;
    }

    public void Execute() => _objects.Add(_obj);
    public void Undo() => _objects.Remove(_obj);
}

class DeleteObjectAction : IEditorAction
{
    private readonly List<AnnotationObject> _objects;
    private readonly AnnotationObject _obj;
    private int _index;
    public string Description => "Delete annotation";

    public DeleteObjectAction(List<AnnotationObject> objects, AnnotationObject obj)
    {
        _objects = objects;
        _obj = obj;
    }

    public void Execute()
    {
        _index = _objects.IndexOf(_obj);
        _objects.Remove(_obj);
    }

    public void Undo() => _objects.Insert(Math.Min(_index, _objects.Count), _obj);
}

class MoveObjectAction : IEditorAction
{
    private readonly AnnotationObject _obj;
    private readonly float _dx, _dy;
    public string Description => "Move annotation";

    public MoveObjectAction(AnnotationObject obj, float dx, float dy)
    {
        _obj = obj;
        _dx = dx;
        _dy = dy;
    }

    public void Execute() => _obj.Move(_dx, _dy);
    public void Undo() => _obj.Move(-_dx, -_dy);
}

class ResizeObjectAction : IEditorAction
{
    private readonly AnnotationObject _obj;
    private readonly HandlePosition _handle;
    private readonly float _dx, _dy;
    public string Description => "Resize annotation";

    public ResizeObjectAction(AnnotationObject obj, HandlePosition handle, float dx, float dy)
    {
        _obj = obj;
        _handle = handle;
        _dx = dx;
        _dy = dy;
    }

    public void Execute() => _obj.Resize(_handle, _dx, _dy);
    public void Undo() => _obj.Resize(_handle, -_dx, -_dy);
}

class ModifyPropertyAction<T> : IEditorAction
{
    private readonly Action<T> _setter;
    private readonly T _oldValue;
    private readonly T _newValue;
    public string Description { get; }

    public ModifyPropertyAction(string description, Action<T> setter, T oldValue, T newValue)
    {
        Description = description;
        _setter = setter;
        _oldValue = oldValue;
        _newValue = newValue;
    }

    public void Execute() => _setter(_newValue);
    public void Undo() => _setter(_oldValue);
}

class ChangeZIndexAction : IEditorAction
{
    private readonly List<AnnotationObject> _objects;
    private readonly AnnotationObject _obj;
    private readonly int _oldIndex;
    private readonly int _newIndex;
    public string Description => "Reorder layer";

    public ChangeZIndexAction(List<AnnotationObject> objects, AnnotationObject obj, int oldIndex, int newIndex)
    {
        _objects = objects;
        _obj = obj;
        _oldIndex = oldIndex;
        _newIndex = newIndex;
    }

    public void Execute()
    {
        _objects.Remove(_obj);
        _objects.Insert(Math.Min(_newIndex, _objects.Count), _obj);
    }

    public void Undo()
    {
        _objects.Remove(_obj);
        _objects.Insert(Math.Min(_oldIndex, _objects.Count), _obj);
    }
}

class CanvasResizeAction : IEditorAction
{
    private readonly EditorSession _session;
    private readonly List<AnnotationObject> _annotations;
    private readonly System.Drawing.Size _oldSize;
    private readonly System.Drawing.Size _newSize;
    private readonly System.Drawing.Point _oldOffset;
    private readonly System.Drawing.Point _newOffset;
    private readonly int _shiftX;
    private readonly int _shiftY;
    public string Description => "Resize canvas";

    public CanvasResizeAction(EditorSession session, System.Drawing.Size oldSize, System.Drawing.Size newSize,
        System.Drawing.Point oldOffset, System.Drawing.Point newOffset, int shiftX, int shiftY)
    {
        _session = session;
        _annotations = session.Annotations;
        _oldSize = oldSize;
        _newSize = newSize;
        _oldOffset = oldOffset;
        _newOffset = newOffset;
        _shiftX = shiftX;
        _shiftY = shiftY;
    }

    public void Execute()
    {
        _session.CanvasSize = _newSize;
        _session.ImageOffset = _newOffset;
        if (_shiftX != 0 || _shiftY != 0)
            foreach (var obj in _annotations)
                obj.Move(_shiftX, _shiftY);
    }

    public void Undo()
    {
        _session.CanvasSize = _oldSize;
        _session.ImageOffset = _oldOffset;
        if (_shiftX != 0 || _shiftY != 0)
            foreach (var obj in _annotations)
                obj.Move(-_shiftX, -_shiftY);
    }
}
