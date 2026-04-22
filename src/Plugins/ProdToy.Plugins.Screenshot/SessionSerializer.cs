using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProdToy.Sdk;

namespace ProdToy.Plugins.Screenshot;

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

    private static readonly object _saveLock = new();

    // Cached FieldInfo for reflection (Fix #5: avoid repeated reflection lookups)
    private static readonly Dictionary<(Type, string), System.Reflection.FieldInfo?> _fieldCache = new();

    /// <summary>Save session state to _edits/{editId}/state.json</summary>
    public static void Save(EditorSession session)
    {
        if (string.IsNullOrEmpty(session.EditId)) return;

        // Lock to prevent concurrent access to Annotations list from UI thread vs StateChanged event
        lock (_saveLock)
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
                state.UndoStack.Add(SerializeAction(action, session));
            foreach (var action in session.UndoRedo.RedoItems.Take(50))
                state.RedoStack.Add(SerializeAction(action, session));

            string dir = session.EditDir;
            Directory.CreateDirectory(dir);
            string json = JsonSerializer.Serialize(state, JsonOpts);
            string filePath = Path.Combine(dir, "state.json");

            // Use FileStream with exclusive write to prevent concurrent edit corruption
            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(fs);
            writer.Write(json);
        }
        catch (Exception ex)
        {
            PluginLog.Error("SessionSerializer.Save failed", ex);
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
                var action = DeserializeAction(actionData, session, session.Annotations, idMap);
                if (action != null)
                    session.UndoRedo.PushUndoRaw(action);
            }
            foreach (var actionData in state.RedoStack)
            {
                var action = DeserializeAction(actionData, session, session.Annotations, idMap);
                if (action != null)
                    session.UndoRedo.PushRedoRaw(action);
            }

            // Find the last CropAction in the undo stack and apply its after-image
            // to reconstruct the OriginalImage (base.png is the uncropped original)
            CropAction? lastCrop = null;
            foreach (var action in session.UndoRedo.UndoItems)
            {
                if (action is CropAction ca) lastCrop = ca;
            }
            if (lastCrop != null)
            {
                session.OriginalImage = (Bitmap)lastCrop.GetAfterImage().Clone();
            }

            return true;
        }
        catch (Exception ex)
        {
            PluginLog.Error("SessionSerializer.Restore failed", ex);
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
            Rotation = obj.Rotation,
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
            case ImageObject imgObj:
                data.PositionX = imgObj.Position.X;
                data.PositionY = imgObj.Position.Y;
                data.DisplayW = imgObj.DisplaySize.Width;
                data.DisplayH = imgObj.DisplaySize.Height;
                data.ImageFile = imgObj.SourcePath ?? "";
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
            case MaskBoxObject mask:
                data.StartX = mask.Start.X; data.StartY = mask.Start.Y;
                data.EndX = mask.End.X; data.EndY = mask.End.Y;
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
            "MaskBoxObject" => new MaskBoxObject
            {
                Start = new PointF(data.StartX, data.StartY),
                End = new PointF(data.EndX, data.EndY),
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
            "ImageObject" when !string.IsNullOrEmpty(data.ImageFile) && File.Exists(data.ImageFile) =>
                LoadImageObject(data),
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
            obj.Rotation = data.Rotation;
        }

        return obj;
    }

    private static ImageObject? LoadImageObject(AnnotationData data)
    {
        try
        {
            Bitmap img;
            using (var stream = File.OpenRead(data.ImageFile!))
            using (var bmp = new Bitmap(stream))
            {
                img = new Bitmap(bmp.Width, bmp.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using var g = Graphics.FromImage(img);
                g.DrawImage(bmp, 0, 0);
            }
            return new ImageObject
            {
                Image = img,
                Position = new PointF(data.PositionX, data.PositionY),
                DisplaySize = new SizeF(
                    data.DisplayW > 0 ? data.DisplayW : img.Width,
                    data.DisplayH > 0 ? data.DisplayH : img.Height),
                SourcePath = data.ImageFile,
            };
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"SessionSerializer LoadImageObject failed: {ex.Message}");
            return null;
        }
    }

    // --- Action serialization ---

    private static ActionData SerializeAction(IEditorAction action, EditorSession? session = null)
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
            case RotateObjectAction rotate:
                data.AnnotationId = GetObjId(rotate);
                data.Dx = GetField<float>(rotate, "_angleDelta");
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
            case CanvasResizeAction canvasResize:
                data.AnnotationId = -1;
                var oldSz = GetField<System.Drawing.Size>(canvasResize, "_oldSize");
                var newSz = GetField<System.Drawing.Size>(canvasResize, "_newSize");
                var oldOff = GetField<System.Drawing.Point>(canvasResize, "_oldOffset");
                var newOff = GetField<System.Drawing.Point>(canvasResize, "_newOffset");
                data.OldSizeW = oldSz.Width; data.OldSizeH = oldSz.Height;
                data.NewSizeW = newSz.Width; data.NewSizeH = newSz.Height;
                data.OldOffsetX = oldOff.X; data.OldOffsetY = oldOff.Y;
                data.NewOffsetX = newOff.X; data.NewOffsetY = newOff.Y;
                data.ShiftX = GetField<int>(canvasResize, "_shiftX");
                data.ShiftY = GetField<int>(canvasResize, "_shiftY");
                break;
            case CropAction crop:
                data.AnnotationId = -1;
                data.CropType = crop.CropType;
                data.CropCorners = crop.Corners.Select(p => new float[] { p.X, p.Y }).ToList();
                // Save before/after images to _edits folder
                string dir = session.EditDir;
                Directory.CreateDirectory(dir);
                string ts = DateTime.Now.ToString("HHmmss_fff");
                if (crop.BeforeImagePath == null)
                {
                    crop.BeforeImagePath = Path.Combine(dir, $"crop_before_{ts}.png");
                    crop.GetBeforeImage().Save(crop.BeforeImagePath, System.Drawing.Imaging.ImageFormat.Png);
                }
                if (crop.AfterImagePath == null)
                {
                    crop.AfterImagePath = Path.Combine(dir, $"crop_after_{ts}.png");
                    crop.GetAfterImage().Save(crop.AfterImagePath, System.Drawing.Imaging.ImageFormat.Png);
                }
                data.CropBeforeFile = Path.GetFileName(crop.BeforeImagePath);
                data.CropAfterFile = Path.GetFileName(crop.AfterImagePath);
                // Save before/after canvas sizes and offset
                var bSz = crop.GetBeforeCanvasSize();
                var bOff = crop.GetBeforeOffset();
                data.OldSizeW = bSz.Width; data.OldSizeH = bSz.Height;
                data.OldOffsetX = bOff.X; data.OldOffsetY = bOff.Y;
                data.NewSizeW = crop.GetAfterImage().Width;
                data.NewSizeH = crop.GetAfterImage().Height;
                // Save before/after annotations
                data.CropBeforeAnnotations = crop.GetBeforeAnnotations().Select(a => SerializeAnnotation(a)).ToList();
                data.CropAfterAnnotations = crop.GetAfterAnnotations().Select(a => SerializeAnnotation(a)).ToList();
                break;
        }

        return data;
    }

    private static IEditorAction? DeserializeAction(ActionData data, EditorSession session,
        List<AnnotationObject> objects, Dictionary<int, AnnotationObject> idMap)
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
            "RotateObjectAction" => FindObj() is { } rotObj
                ? new RotateObjectAction(rotObj, data.Dx)
                : null,
            "ChangeZIndexAction" => FindObj() is { } zObj
                ? new ChangeZIndexAction(objects, zObj, data.OldIndex, data.NewIndex)
                : null,
            "CanvasResizeAction" => new CanvasResizeAction(session,
                new Size(data.OldSizeW, data.OldSizeH),
                new Size(data.NewSizeW, data.NewSizeH),
                new Point(data.OldOffsetX, data.OldOffsetY),
                new Point(data.NewOffsetX, data.NewOffsetY),
                data.ShiftX, data.ShiftY),
            "CropAction" => DeserializeCropAction(data, session),
            _ => null,
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
        var field = GetCachedField(action.GetType(), "_obj");
        return field?.GetValue(action) as AnnotationObject;
    }

    private static T GetField<T>(object action, string fieldName)
    {
        var field = GetCachedField(action.GetType(), fieldName);
        return field != null ? (T)field.GetValue(action)! : default!;
    }

    private static System.Reflection.FieldInfo? GetCachedField(Type type, string fieldName)
    {
        var key = (type, fieldName);
        if (_fieldCache.TryGetValue(key, out var cached))
            return cached;

        var field = type.GetField(fieldName,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        _fieldCache[key] = field;
        return field;
    }

    // --- Color helpers ---

    private static CropAction? DeserializeCropAction(ActionData data, EditorSession session)
    {
        try
        {
            string dir = session.EditDir;
            string beforePath = Path.Combine(dir, data.CropBeforeFile ?? "");
            string afterPath = Path.Combine(dir, data.CropAfterFile ?? "");
            if (!File.Exists(beforePath) || !File.Exists(afterPath)) return null;

            Bitmap LoadBmp(string path)
            {
                using var stream = File.OpenRead(path);
                using var bmp = new Bitmap(stream);
                var img = new Bitmap(bmp.Width, bmp.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using var g = Graphics.FromImage(img);
                g.DrawImage(bmp, 0, 0);
                return img;
            }

            var beforeImage = LoadBmp(beforePath);
            var afterImage = LoadBmp(afterPath);
            var corners = data.CropCorners?.Select(p => new PointF(p[0], p[1])).ToArray() ?? new PointF[4];
            var beforeAnnotations = (data.CropBeforeAnnotations ?? new())
                .Select(a => DeserializeAnnotation(a)).Where(a => a != null).Cast<AnnotationObject>().ToList();
            var afterAnnotations = (data.CropAfterAnnotations ?? new())
                .Select(a => DeserializeAnnotation(a)).Where(a => a != null).Cast<AnnotationObject>().ToList();

            var action = new CropAction(session,
                beforeImage, new Size(data.OldSizeW, data.OldSizeH), new Point(data.OldOffsetX, data.OldOffsetY),
                beforeAnnotations,
                afterImage, new Size(data.NewSizeW, data.NewSizeH),
                afterAnnotations,
                data.CropType ?? "perspective", corners);
            action.BeforeImagePath = beforePath;
            action.AfterImagePath = afterPath;
            return action;
        }
        catch { return null; }
    }

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
    [JsonPropertyName("rotation")] public float Rotation { get; set; }

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

    // Image object
    [JsonPropertyName("imageFile")] public string? ImageFile { get; set; }
    [JsonPropertyName("displayW")] public float DisplayW { get; set; }
    [JsonPropertyName("displayH")] public float DisplayH { get; set; }
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

    // Canvas resize
    [JsonPropertyName("oldSizeW")] public int OldSizeW { get; set; }
    [JsonPropertyName("oldSizeH")] public int OldSizeH { get; set; }
    [JsonPropertyName("newSizeW")] public int NewSizeW { get; set; }
    [JsonPropertyName("newSizeH")] public int NewSizeH { get; set; }
    [JsonPropertyName("oldOffsetX")] public int OldOffsetX { get; set; }
    [JsonPropertyName("oldOffsetY")] public int OldOffsetY { get; set; }
    [JsonPropertyName("newOffsetX")] public int NewOffsetX { get; set; }
    [JsonPropertyName("newOffsetY")] public int NewOffsetY { get; set; }
    [JsonPropertyName("shiftX")] public int ShiftX { get; set; }
    [JsonPropertyName("shiftY")] public int ShiftY { get; set; }

    // Crop action
    [JsonPropertyName("cropType")] public string? CropType { get; set; }
    [JsonPropertyName("cropCorners")] public List<float[]>? CropCorners { get; set; }
    [JsonPropertyName("cropBeforeFile")] public string? CropBeforeFile { get; set; }
    [JsonPropertyName("cropAfterFile")] public string? CropAfterFile { get; set; }
    [JsonPropertyName("cropBeforeAnnotations")] public List<AnnotationData>? CropBeforeAnnotations { get; set; }
    [JsonPropertyName("cropAfterAnnotations")] public List<AnnotationData>? CropAfterAnnotations { get; set; }
}
