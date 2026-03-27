using System.Drawing;
using System.Drawing.Drawing2D;

namespace DevToy;

class PenStroke : AnnotationObject
{
    public List<PointF> Points { get; set; } = new();

    // Cached bounds to avoid O(n) recalculation on every call
    private RectangleF? _cachedBounds;
    private int _cachedPointCount;
    private float _cachedThickness;

    public void InvalidateBoundsCache() => _cachedBounds = null;

    public override RectangleF GetBounds()
    {
        if (Points.Count == 0) return RectangleF.Empty;

        // Return cached if points/thickness haven't changed
        if (_cachedBounds.HasValue && _cachedPointCount == Points.Count && Math.Abs(_cachedThickness - Thickness) < 0.01f)
            return _cachedBounds.Value;

        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        foreach (var p in Points)
        {
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
        }
        float pad = Thickness / 2 + 2;
        _cachedBounds = new RectangleF(minX - pad, minY - pad, maxX - minX + pad * 2, maxY - minY + pad * 2);
        _cachedPointCount = Points.Count;
        _cachedThickness = Thickness;
        return _cachedBounds.Value;
    }

    public override void Render(Graphics g)
    {
        if (Points.Count < 2) return;
        using var pen = CreatePen();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.DrawLines(pen, Points.ToArray());
    }

    public override bool HitTest(PointF point, float tolerance)
    {
        float tol = Math.Max(tolerance, Thickness / 2 + 4);

        // Quick bounds check to skip expensive segment iteration
        var bounds = GetBounds();
        bounds.Inflate(tol, tol);
        if (!bounds.Contains(point)) return false;

        for (int i = 1; i < Points.Count; i++)
        {
            if (DistanceToSegment(point, Points[i - 1], Points[i]) < tol)
                return true;
        }
        return false;
    }

    public override void Move(float dx, float dy)
    {
        for (int i = 0; i < Points.Count; i++)
            Points[i] = new PointF(Points[i].X + dx, Points[i].Y + dy);
        InvalidateBoundsCache();
    }

    public override AnnotationObject Clone()
    {
        return new PenStroke
        {
            Points = new List<PointF>(Points),
            StrokeColor = StrokeColor,
            Thickness = Thickness,
            Opacity = Opacity,
            ZIndex = ZIndex,
        };
    }

    public static float DistanceToSegmentStatic(PointF p, PointF a, PointF b) => DistanceToSegment(p, a, b);

    protected static float DistanceToSegment(PointF p, PointF a, PointF b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float lenSq = dx * dx + dy * dy;
        if (lenSq < 0.001f) return Distance(p, a);
        float t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq, 0, 1);
        var proj = new PointF(a.X + t * dx, a.Y + t * dy);
        return Distance(p, proj);
    }

    protected static float Distance(PointF a, PointF b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
