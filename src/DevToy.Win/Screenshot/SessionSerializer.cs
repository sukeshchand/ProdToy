using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevToy;

/// <summary>
/// Serializes/deserializes the full editor session state to/from JSON
/// in the _edits folder. Saves annotations, canvas settings, border, colors.
/// </summary>
static class SessionSerializer
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Save session state to _edits/{editId}/state.json</summary>
    public static void Save(EditorSession session)
    {
        if (string.IsNullOrEmpty(session.EditId)) return;

        try
        {
            var state = new SessionState
            {
                EditId = session.EditId,
                CanvasWidth = session.CanvasSize.Width,
                CanvasHeight = session.CanvasSize.Height,
                ImageOffsetX = session.ImageOffset.X,
                ImageOffsetY = session.ImageOffset.Y,
                BgColor = ToHex(session.CanvasBackgroundColor),
                CurrentTool = session.CurrentTool.ToString(),
                CurrentColor = ToHex(session.CurrentColor),
                CurrentThickness = session.CurrentThickness,
                CurrentFontSize = session.CurrentFontSize,
                BorderEnabled = session.BorderEnabled,
                BorderStyle = session.BorderStyle.ToString(),
                BorderColor = ToHex(session.BorderColor),
                BorderThickness = session.BorderThickness,
                Timestamp = DateTime.Now,
            };

            foreach (var obj in session.Annotations)
                state.Annotations.Add(SerializeAnnotation(obj));

            // Serialize undo/redo stacks (max 50 each)
            foreach (var action in session.UndoRedo.UndoItems.Take(50))
                state.UndoStack.Add(SerializeAction(action));
            foreach (var action in session.UndoRedo.RedoItems.Take(50))
                state.RedoStack.Add(SerializeAction(action));

            string dir = session.EditDir;
            Directory.CreateDirectory(dir);
            string json = JsonSerializer.Serialize(state, JsonOpts);
            File.WriteAllText(Path.Combine(dir, "state.json"), json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SessionSerializer.Save failed: {ex.Message}");
        }
    }

    /// <summary>Restore annotations and settings from _edits/{editId}/state.json into the session.</summary>
    public static bool Restore(EditorSession session)
    {
        if (string.IsNullOrEmpty(session.EditId)) return false;

        try
        {
            string path = Path.Combine(session.EditDir, "state.json");
            if (!File.Exists(path)) return false;

            string json = File.ReadAllText(path);
            var state = JsonSerializer.Deserialize<SessionState>(json, JsonOpts);
            if (state == null) return false;

            session.CanvasSize = new Size(state.CanvasWidth, state.CanvasHeight);
            session.ImageOffset = new Point(state.ImageOffsetX, state.ImageOffsetY);
            session.CanvasBackgroundColor = FromHex(state.BgColor, Color.White);
            session.CurrentColor = FromHex(state.CurrentColor, Color.Red);
            session.CurrentThickness = state.CurrentThickness;
            session.CurrentFontSize = state.CurrentFontSize;
            session.BorderEnabled = state.BorderEnabled;
            session.BorderColor = FromHex(state.BorderColor, Color.FromArgb(60, 60, 60));
            session.BorderThickness = state.BorderThickness;

            if (Enum.TryParse<AnnotationTool>(state.CurrentTool, out var tool))
                session.CurrentTool = tool;
            if (Enum.TryParse<CanvasBorderStyle>(state.BorderStyle, out var bs))
                session.BorderStyle = bs;

            // Restore annotations directly (not through undo system)
            session.Annotations.Clear();
            foreach (var data in state.Annotations)
            {
                var obj = DeserializeAnnotation(data);
                if (obj != null)
                    session.Annotations.Add(obj);
            }

            // Build ID lookup for action restoration
            var idMap = new Dictionary<int, AnnotationObject>();
            foreach (var obj in session.Annotations)
                idMap[obj.Id] = obj;

            // Restore undo stack (push in order so first item is at bottom)
            session.UndoRedo.Clear();
            foreach (var actionData in state.UndoStack)
            {
                var action = DeserializeAction(actionData, session.Annotations, idMap);
                if (action != null)
                    session.UndoRedo.PushUndoRaw(action);
            }
            foreach (var actionData in state.RedoStack)
            {
                var action = DeserializeAction(actionData, session.Annotations, idMap);
                if (action != null)
                    session.UndoRedo.PushRedoRaw(action);
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SessionSerializer.Restore failed: {ex.Message}");
            return false;
        }
    }

    // --- Annotation serialization ---

    private static AnnotationData SerializeAnnotation(AnnotationObject obj)
    {
        var data = new AnnotationData
        {
            Id = obj.Id,
            Type = obj.GetType().Name,
            StrokeColor = ToHex(obj.StrokeColor),
            FillColor = ToHex(obj.FillColor),
            Thickness = obj.Thickness,
            Opacity = obj.Opacity,
            ZIndex = obj.ZIndex,
        };

        switch (obj)
        {
            case MarkerStroke ms:
                data.Points = ms.Points.Select(p => new float[] { p.X, p.Y }).ToList();
                break;
            case PenStroke ps:
                data.Points = ps.Points.Select(p => new float[] { p.X, p.Y }).ToList();
                break;
            case TextObject txt:
                data.Text = txt.Text;
                data.PositionX = txt.Position.X;
                data.PositionY = txt.Position.Y;
                data.FontSize = txt.FontSize;
                data.Bold = txt.Bold;
                break;
            case RectangleObject rect:
                data.StartX = rect.Start.X; data.StartY = rect.Start.Y;
                data.EndX = rect.End.X; data.EndY = rect.End.Y;
                data.Filled = rect.Filled;
                break;
            case EllipseObject ell:
                data.StartX = ell.Start.X; data.StartY = ell.Start.Y;
                data.EndX = ell.End.X; data.EndY = ell.End.Y;
                data.Filled = ell.Filled;
                break;
            case ArrowObject arr:
                data.StartX = arr.Start.X; data.StartY = arr.Start.Y;
                data.EndX = arr.End.X; data.EndY = arr.End.Y;
                break;
            case LineObject line:
                data.StartX = line.Start.X; data.StartY = line.Start.Y;
                data.EndX = line.End.X; data.EndY = line.End.Y;
                break;
        }

        return data;
    }

    private static AnnotationObject? DeserializeAnnotation(AnnotationData data)
    {
        AnnotationObject? obj = data.Type switch
        {
            "PenStroke" => new PenStroke
            {
                Points = data.Points?.Select(p => new PointF(p[0], p[1])).ToList() ?? new(),
            },
            "MarkerStroke" => new MarkerStroke
            {
                Points = data.Points?.Select(p => new PointF(p[0], p[1])).ToList() ?? new(),
            },
            "LineObject" => new LineObject
            {
                Start = new PointF(data.StartX, data.StartY),
                End = new PointF(data.EndX, data.EndY),
            },
            "ArrowObject" => new ArrowObject
            {
                Start = new PointF(data.StartX, data.StartY),
                End = new PointF(data.EndX, data.EndY),
            },
            "RectangleObject" => new RectangleObject
            {
                Start = new PointF(data.StartX, data.StartY),
                End = new PointF(data.EndX, data.EndY),
                Filled = data.Filled,
            },
            "EllipseObject" => new EllipseObject
            {
                Start = new PointF(data.StartX, data.StartY),
                End = new PointF(data.EndX, data.EndY),
                Filled = data.Filled,
            },
            "TextObject" => new TextObject
            {
                Text = data.Text ?? "",
                Position = new PointF(data.PositionX, data.PositionY),
                FontSize = data.FontSize > 0 ? data.FontSize : 16f,
                Bold = data.Bold,
            },
            _ => null,
        };

        if (obj != null)
        {
            if (data.Id > 0) obj.Id = data.Id;
            obj.StrokeColor = FromHex(data.StrokeColor, Color.Red);
            obj.FillColor = FromHex(data.FillColor, Color.Transparent);
            obj.Thickness = data.Thickness;
            obj.Opacity = data.Opacity;
            obj.ZIndex = data.ZIndex;
        }

        return obj;
    }

    // --- Action serialization ---

    private static ActionData SerializeAction(IEditorAction action)
    {
        var data = new ActionData { Type = action.GetType().Name, Description = action.Description };

        switch (action)
        {
            case AddObjectAction add:
                data.AnnotationId = GetObjId(add);
                data.Annotation = SerializeAnnotation(GetObj(add));
                break;
            case DeleteObjectAction del:
                data.AnnotationId = GetObjId(del);
                data.Annotation = SerializeAnnotation(GetObj(del));
                break;
            case MoveObjectAction move:
                data.AnnotationId = GetObjId(move);
                data.Dx = GetField<float>(move, "_dx");
                data.Dy = GetField<float>(move, "_dy");
                break;
            case ResizeObjectAction resize:
                data.AnnotationId = GetObjId(resize);
                data.Dx = GetField<float>(resize, "_dx");
                data.Dy = GetField<float>(resize, "_dy");
                data.Handle = GetField<HandlePosition>(resize, "_handle").ToString();
                break;
            case ChangeZIndexAction zIndex:
                data.AnnotationId = GetObjId(zIndex);
                data.OldIndex = GetField<int>(zIndex, "_oldIndex");
                data.NewIndex = GetField<int>(zIndex, "_newIndex");
                break;
            case ModifyPropertyAction<Color> colorProp:
                data.AnnotationId = -1; // property actions don't always have a target obj
                data.PropertyOld = ToHex(GetField<Color>(colorProp, "_oldValue"));
                data.PropertyNew = ToHex(GetField<Color>(colorProp, "_newValue"));
                break;
            case ModifyPropertyAction<float> floatProp:
                data.AnnotationId = -1;
                data.Dx = GetField<float>(floatProp, "_oldValue");
                data.Dy = GetField<float>(floatProp, "_newValue");
                break;
        }

        return data;
    }

    private static IEditorAction? DeserializeAction(ActionData data, List<AnnotationObject> objects,
        Dictionary<int, AnnotationObject> idMap)
    {
        AnnotationObject? FindObj() => data.AnnotationId > 0 && idMap.TryGetValue(data.AnnotationId, out var o) ? o : null;

        return data.Type switch
        {
            "AddObjectAction" => data.Annotation != null
                ? new AddObjectAction(objects, FindObj() ?? DeserializeAnnotation(data.Annotation)!)
                : null,
            "DeleteObjectAction" => FindObj() is { } delObj
                ? new DeleteObjectAction(objects, delObj)
                : null,
            "MoveObjectAction" => FindObj() is { } moveObj
                ? new MoveObjectAction(moveObj, data.Dx, data.Dy)
                : null,
            "ResizeObjectAction" => FindObj() is { } resizeObj &&
                Enum.TryParse<HandlePosition>(data.Handle, out var h)
                ? new ResizeObjectAction(resizeObj, h, data.Dx, data.Dy)
                : null,
            "ChangeZIndexAction" => FindObj() is { } zObj
                ? new ChangeZIndexAction(objects, zObj, data.OldIndex, data.NewIndex)
                : null,
            _ => null, // ModifyPropertyAction can't be reliably restored (lambda setters)
        };
    }

    // Reflection helpers to read private fields from action objects
    private static int GetObjId(object action)
    {
        var obj = GetObj(action);
        return obj?.Id ?? -1;
    }

    private static AnnotationObject? GetObj(object action)
    {
        var field = action.GetType().GetField("_obj",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return field?.GetValue(action) as AnnotationObject;
    }

    private static T GetField<T>(object action, string fieldName)
    {
        var field = action.GetType().GetField(fieldName,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return field != null ? (T)field.GetValue(action)! : default!;
    }

    // --- Color helpers ---

    private static string ToHex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    private static Color FromHex(string? hex, Color fallback)
    {
        if (string.IsNullOrEmpty(hex) || hex.Length < 7) return fallback;
        try
        {
            hex = hex.TrimStart('#');
            if (hex.Length == 8) // ARGB
                return Color.FromArgb(
                    Convert.ToInt32(hex[..2], 16),
                    Convert.ToInt32(hex[2..4], 16),
                    Convert.ToInt32(hex[4..6], 16),
                    Convert.ToInt32(hex[6..8], 16));
            if (hex.Length == 6) // RGB
                return Color.FromArgb(
                    Convert.ToInt32(hex[..2], 16),
                    Convert.ToInt32(hex[2..4], 16),
                    Convert.ToInt32(hex[4..6], 16));
        }
        catch { }
        return fallback;
    }
}

// --- JSON data models ---

class SessionState
{
    [JsonPropertyName("editId")] public string EditId { get; set; } = "";
    [JsonPropertyName("canvasWidth")] public int CanvasWidth { get; set; }
    [JsonPropertyName("canvasHeight")] public int CanvasHeight { get; set; }
    [JsonPropertyName("imageOffsetX")] public int ImageOffsetX { get; set; }
    [JsonPropertyName("imageOffsetY")] public int ImageOffsetY { get; set; }
    [JsonPropertyName("bgColor")] public string? BgColor { get; set; }
    [JsonPropertyName("currentTool")] public string? CurrentTool { get; set; }
    [JsonPropertyName("currentColor")] public string? CurrentColor { get; set; }
    [JsonPropertyName("currentThickness")] public float CurrentThickness { get; set; }
    [JsonPropertyName("currentFontSize")] public float CurrentFontSize { get; set; }
    [JsonPropertyName("borderEnabled")] public bool BorderEnabled { get; set; }
    [JsonPropertyName("borderStyle")] public string? BorderStyle { get; set; }
    [JsonPropertyName("borderColor")] public string? BorderColor { get; set; }
    [JsonPropertyName("borderThickness")] public float BorderThickness { get; set; }
    [JsonPropertyName("timestamp")] public DateTime Timestamp { get; set; }
    [JsonPropertyName("annotations")] public List<AnnotationData> Annotations { get; set; } = new();
    [JsonPropertyName("undoStack")] public List<ActionData> UndoStack { get; set; } = new();
    [JsonPropertyName("redoStack")] public List<ActionData> RedoStack { get; set; } = new();
}

class AnnotationData
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("strokeColor")] public string? StrokeColor { get; set; }
    [JsonPropertyName("fillColor")] public string? FillColor { get; set; }
    [JsonPropertyName("thickness")] public float Thickness { get; set; }
    [JsonPropertyName("opacity")] public float Opacity { get; set; }
    [JsonPropertyName("zIndex")] public int ZIndex { get; set; }

    // Points (for PenStroke, MarkerStroke)
    [JsonPropertyName("points")] public List<float[]>? Points { get; set; }

    // Shape start/end
    [JsonPropertyName("startX")] public float StartX { get; set; }
    [JsonPropertyName("startY")] public float StartY { get; set; }
    [JsonPropertyName("endX")] public float EndX { get; set; }
    [JsonPropertyName("endY")] public float EndY { get; set; }
    [JsonPropertyName("filled")] public bool Filled { get; set; }

    // Text
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("positionX")] public float PositionX { get; set; }
    [JsonPropertyName("positionY")] public float PositionY { get; set; }
    [JsonPropertyName("fontSize")] public float FontSize { get; set; }
    [JsonPropertyName("bold")] public bool Bold { get; set; }
}

class ActionData
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("annotationId")] public int AnnotationId { get; set; }
    [JsonPropertyName("annotation")] public AnnotationData? Annotation { get; set; }
    [JsonPropertyName("dx")] public float Dx { get; set; }
    [JsonPropertyName("dy")] public float Dy { get; set; }
    [JsonPropertyName("handle")] public string? Handle { get; set; }
    [JsonPropertyName("oldIndex")] public int OldIndex { get; set; }
    [JsonPropertyName("newIndex")] public int NewIndex { get; set; }
    [JsonPropertyName("propertyOld")] public string? PropertyOld { get; set; }
    [JsonPropertyName("propertyNew")] public string? PropertyNew { get; set; }
}
