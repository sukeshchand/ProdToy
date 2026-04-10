using System.Drawing;
using System.Drawing.Drawing2D;

namespace ProdToy.Plugins.Screenshot;

enum AnnotationTool
{
    Select,
    Pen,
    Marker,
    Line,
    Arrow,
    Rectangle,
    Ellipse,
    Text,
    Eraser,
    MaskBox,
    Crop,
}

abstract class AnnotationObject
{
    private static int _nextId;

    public int Id { get; set; } = Interlocked.Increment(ref _nextId);
    public Color StrokeColor { get; set; } = Color.Red;
    public Color FillColor { get; set; } = Color.Transparent;
    public float Thickness { get; set; } = 2f;
    public float Opacity { get; set; } = 1f;
    public int ZIndex { get; set; }
    public bool IsSelected { get; set; }

    /// <summary>Rotation angle in degrees (clockwise).</summary>
    public float Rotation { get; set; }

    public abstract RectangleF GetBounds();
    public abstract void Render(Graphics g);
    public abstract bool HitTest(PointF point, float tolerance);
    public abstract AnnotationObject Clone();

    /// <summary>Move the object by a delta offset.</summary>
    public abstract void Move(float dx, float dy);

    /// <summary>Apply rotation transform around the object's center. Call g.Restore(state) when done.</summary>
    protected GraphicsState ApplyRotation(Graphics g)
    {
        if (Math.Abs(Rotation) < 0.01f) return g.Save();
        var state = g.Save();
        var center = GetCenter();
        g.TranslateTransform(center.X, center.Y);
        g.RotateTransform(Rotation);
        g.TranslateTransform(-center.X, -center.Y);
        return state;
    }

    protected Pen CreatePen()
    {
        var color = Opacity < 1f
            ? Color.FromArgb((int)(Opacity * 255), StrokeColor)
            : StrokeColor;
        return new Pen(color, Thickness)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
    }

    protected SolidBrush CreateBrush()
    {
        var color = Opacity < 1f
            ? Color.FromArgb((int)(Opacity * 255), StrokeColor)
            : StrokeColor;
        return new SolidBrush(color);
    }

    /// <summary>Center point of the bounding box (used as rotation pivot).</summary>
    public PointF GetCenter()
    {
        var b = GetBounds();
        return new PointF(b.X + b.Width / 2, b.Y + b.Height / 2);
    }

    /// <summary>Render selection handles around the bounding rect, rotated.</summary>
    public void RenderSelectionHandles(Graphics g)
    {
        if (!IsSelected) return;
        var bounds = GetBounds();
        var center = GetCenter();

        var state = g.Save();
        g.TranslateTransform(center.X, center.Y);
        g.RotateTransform(Rotation);
        g.TranslateTransform(-center.X, -center.Y);

        using var pen = new Pen(Color.FromArgb(200, 80, 160, 255), 1.5f) { DashStyle = DashStyle.Dash };
        g.DrawRectangle(pen, bounds.X, bounds.Y, bounds.Width, bounds.Height);

        float hs = 6f;
        using var brush = new SolidBrush(Color.FromArgb(220, 80, 160, 255));
        var handles = GetHandlePoints(bounds, hs);
        foreach (var h in handles)
            g.FillRectangle(brush, h);

        // Rotation handle: circle above top-center, connected by a line
        float mx = bounds.X + bounds.Width / 2;
        float rotHandleY = bounds.Top - 24;
        using var linePen = new Pen(Color.FromArgb(160, 80, 160, 255), 1f);
        g.DrawLine(linePen, mx, bounds.Top, mx, rotHandleY + 5);
        g.FillEllipse(brush, mx - 5, rotHandleY - 5, 10, 10);

        g.Restore(state);
    }

    public HandlePosition HitTestHandle(PointF point, float tolerance = 8f)
    {
        var bounds = GetBounds();
        // Transform point into unrotated local space
        var local = RotatePoint(point, GetCenter(), -Rotation);

        // Check rotation handle first
        float mx = bounds.X + bounds.Width / 2;
        float rotHandleY = bounds.Top - 24;
        if (Math.Abs(local.X - mx) < tolerance && Math.Abs(local.Y - rotHandleY) < tolerance)
            return HandlePosition.Rotate;

        float hs = 6f;
        var handles = GetHandlePoints(bounds, hs);
        var positions = new[]
        {
            HandlePosition.TopLeft, HandlePosition.TopCenter, HandlePosition.TopRight,
            HandlePosition.MiddleLeft, HandlePosition.MiddleRight,
            HandlePosition.BottomLeft, HandlePosition.BottomCenter, HandlePosition.BottomRight,
        };
        for (int i = 0; i < handles.Length; i++)
        {
            var inflated = handles[i];
            inflated.Inflate(tolerance / 2, tolerance / 2);
            if (inflated.Contains(local))
                return positions[i];
        }
        return HandlePosition.None;
    }

    /// <summary>Rotate a point around a center by given degrees.</summary>
    public static PointF RotatePoint(PointF point, PointF center, float degrees)
    {
        float rad = degrees * MathF.PI / 180f;
        float cos = MathF.Cos(rad);
        float sin = MathF.Sin(rad);
        float dx = point.X - center.X;
        float dy = point.Y - center.Y;
        return new PointF(
            center.X + dx * cos - dy * sin,
            center.Y + dx * sin + dy * cos);
    }

    public virtual void Resize(HandlePosition handle, float dx, float dy) { }

    private static RectangleF[] GetHandlePoints(RectangleF b, float hs)
    {
        float hh = hs / 2;
        float mx = b.X + b.Width / 2;
        float my = b.Y + b.Height / 2;
        return new[]
        {
            new RectangleF(b.Left - hh, b.Top - hh, hs, hs),
            new RectangleF(mx - hh, b.Top - hh, hs, hs),
            new RectangleF(b.Right - hh, b.Top - hh, hs, hs),
            new RectangleF(b.Left - hh, my - hh, hs, hs),
            new RectangleF(b.Right - hh, my - hh, hs, hs),
            new RectangleF(b.Left - hh, b.Bottom - hh, hs, hs),
            new RectangleF(mx - hh, b.Bottom - hh, hs, hs),
            new RectangleF(b.Right - hh, b.Bottom - hh, hs, hs),
        };
    }
}

enum HandlePosition
{
    None,
    TopLeft, TopCenter, TopRight,
    MiddleLeft, MiddleRight,
    BottomLeft, BottomCenter, BottomRight,
    Rotate,
}
