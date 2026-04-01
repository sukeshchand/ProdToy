using System.Drawing;

namespace ProdToy;

class EditorSession
{
    public Bitmap OriginalImage { get; set; }
    public string EditId { get; set; } = "";
    public string EditDir => Path.Combine(AppPaths.ScreenshotsEditsDir, EditId);
    public List<AnnotationObject> Annotations { get; } = new();
    public UndoRedoManager UndoRedo { get; }
    public AnnotationTool CurrentTool { get; set; } = AnnotationTool.Select;
    public Color CurrentColor { get; set; } = Color.Red;
    public float CurrentThickness { get; set; } = 2f;
    public float CurrentFontSize { get; set; } = 16f;
    public AnnotationObject? SelectedObject { get; set; }

    /// <summary>
    /// Offset where the original image is drawn within the canvas.
    /// When canvas expands left/top, this increases so the image stays in place.
    /// </summary>
    public Point ImageOffset { get; set; } = Point.Empty;

    /// <summary>Current canvas size (may be larger than OriginalImage).</summary>
    public Size CanvasSize { get; set; }

    // Canvas background
    public Color CanvasBackgroundColor { get; set; } = Color.White;

    // Border settings
    public bool BorderEnabled { get; set; }
    public CanvasBorderStyle BorderStyle { get; set; } = CanvasBorderStyle.Solid;
    public Color BorderColor { get; set; } = Color.FromArgb(60, 60, 60);
    public float BorderThickness { get; set; } = 2f;

    public EditorSession(Bitmap image, int maxUndo = 30)
    {
        OriginalImage = image;
        CanvasSize = image.Size;
        UndoRedo = new UndoRedoManager(maxUndo);
    }

    public void AddAnnotation(AnnotationObject obj)
    {
        obj.ZIndex = Annotations.Count;
        UndoRedo.Execute(new AddObjectAction(Annotations, obj));
    }

    public void DeleteSelected()
    {
        if (SelectedObject == null) return;
        var obj = SelectedObject;
        obj.IsSelected = false;
        SelectedObject = null;
        UndoRedo.Execute(new DeleteObjectAction(Annotations, obj));
    }

    public void MoveSelected(float dx, float dy)
    {
        if (SelectedObject == null) return;
        UndoRedo.Execute(new MoveObjectAction(SelectedObject, dx, dy));
    }

    public void BringForward()
    {
        if (SelectedObject == null) return;
        int idx = Annotations.IndexOf(SelectedObject);
        if (idx < 0 || idx >= Annotations.Count - 1) return;
        UndoRedo.Execute(new ChangeZIndexAction(Annotations, SelectedObject, idx, idx + 1));
    }

    public void SendBackward()
    {
        if (SelectedObject == null) return;
        int idx = Annotations.IndexOf(SelectedObject);
        if (idx <= 0) return;
        UndoRedo.Execute(new ChangeZIndexAction(Annotations, SelectedObject, idx, idx - 1));
    }

    public void SelectAt(PointF point)
    {
        // Deselect current
        if (SelectedObject != null)
        {
            SelectedObject.IsSelected = false;
            SelectedObject = null;
        }

        // Hit test from top (highest z-order = last in list) to bottom
        for (int i = Annotations.Count - 1; i >= 0; i--)
        {
            if (Annotations[i].HitTest(point, 6f))
            {
                SelectedObject = Annotations[i];
                SelectedObject.IsSelected = true;
                return;
            }
        }
    }

    public void DeselectAll()
    {
        if (SelectedObject != null)
        {
            SelectedObject.IsSelected = false;
            SelectedObject = null;
        }
        // Also stop editing any text objects
        foreach (var obj in Annotations)
        {
            if (obj is TextObject txt) txt.IsEditing = false;
        }
    }
}

enum CanvasBorderStyle
{
    Solid,
    Dashed,
    Dotted,
    Double,
    Shadow,
}
