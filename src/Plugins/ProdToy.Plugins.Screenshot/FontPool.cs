using System.Drawing;

namespace ProdToy.Plugins.Screenshot;

/// <summary>
/// Caches Font objects to avoid creating/disposing hundreds per second during paint cycles.
/// Fonts are pooled by (family, size, style) key. Disposed when pool is cleared.
/// </summary>
static class FontPool
{
    private static readonly Dictionary<(string Family, float Size, FontStyle Style), Font> _cache = new();

    public static Font Get(string family, float size, FontStyle style = FontStyle.Regular)
    {
        var key = (family, size, style);
        if (_cache.TryGetValue(key, out var font))
            return font;

        font = new Font(family, size, style);
        _cache[key] = font;
        return font;
    }

    public static void Clear()
    {
        foreach (var font in _cache.Values)
            font.Dispose();
        _cache.Clear();
    }
}
