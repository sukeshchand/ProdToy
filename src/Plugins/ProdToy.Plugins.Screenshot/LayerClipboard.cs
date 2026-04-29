namespace ProdToy.Plugins.Screenshot;

/// <summary>
/// Process-wide internal clipboard for a single annotation/layer copied
/// inside the Screenshot editor. Survives across captures and edits but is
/// cleared on app exit. Stores a Clone() of the source so callers can keep
/// mutating the original; Take() also returns a fresh Clone() so each paste
/// is independent.
/// </summary>
static class LayerClipboard
{
    private static AnnotationObject? _stored;

    public static bool HasContent => _stored != null;

    public static void Set(AnnotationObject obj) => _stored = obj.Clone();

    /// <summary>Returns a fresh clone, or null if empty.</summary>
    public static AnnotationObject? Take() => _stored?.Clone();

    public static void Clear() => _stored = null;
}
