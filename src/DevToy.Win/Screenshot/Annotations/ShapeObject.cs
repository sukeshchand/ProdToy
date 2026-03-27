using System.Drawing;
using System.Drawing.Drawing2D;

namespace DevToy;

/// <summary>Base for two-point shapes (line, arrow, rectangle, ellipse).</summary>
abstract class ShapeObject : AnnotationObject
{
    public PointF Start { get; set; }
    public PointF End { get; set; }

    public override RectangleF GetBounds()
    {
        float pad = Thickness / 2 + 4;
        float x = Math.Min(Start.X, End.X) - pad;
        float y = Math.Min(Start.Y, End.Y) - pad;
        float w = Math.Abs(End.X - Start.X) + pad * 2;
        float h = Math.Abs(End.Y - Start.Y) + pad * 2;
        return new RectangleF(x, y, w, h);
    }

    protected RectangleF GetShapeRect()
    {
        float x = Math.Min(Start.X, End.X);
        float y = Math.Min(Start.Y, End.Y);
        float w = Math.Abs(End.X - Start.X);
        float h = Math.Abs(End.Y - Start.Y);
        return new RectangleF(x, y, Math.Max(1, w), Math.Max(1, h));
    }

    public override void Move(float dx, float dy)
    {
        Start = new PointF(Start.X + dx, Start.Y + dy);
        End = new PointF(End.X + dx, End.Y + dy);
    }

    public override void Resize(HandlePosition handle, float dx, float dy)
    {
        switch (handle)
        {
            case HandlePosition.TopLeft:
                Start = new PointF(Start.X + dx, Start.Y + dy);
                break;
            case HandlePosition.TopCenter:
                Start = new PointF(Start.X, Start.Y + dy);
                break;
            case HandlePosition.TopRight:
                Start = new PointF(Start.X, Start.Y + dy);
                End = new PointF(End.X + dx, End.Y);
                break;
            case HandlePosition.MiddleLeft:
                Start = new PointF(Start.X + dx, Start.Y);
                break;
            case HandlePosition.MiddleRight:
                End = new PointF(End.X + dx, End.Y);
                break;
            case HandlePosition.BottomLeft:
                Start = new PointF(Start.X + dx, Start.Y);
                End = new PointF(End.X, End.Y + dy);
                break;
            case HandlePosition.BottomCenter:
                End = new PointF(End.X, End.Y + dy);
                break;
            case HandlePosition.BottomRight:
                End = new PointF(End.X + dx, End.Y + dy);
                break;
        }
    }
}

class LineObject : ShapeObject
{
    public override void Render(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = CreatePen();
        g.DrawLine(pen, Start, End);
    }

    public override bool HitTest(PointF point, float tolerance)
    {
        return PenStroke.DistanceToSegmentStatic(point, Start, End) < Math.Max(tolerance, Thickness / 2 + 4);
    }

    public override AnnotationObject Clone() => new LineObject
    {
        Start = Start, End = End, StrokeColor = StrokeColor,
        Thickness = Thickness, Opacity = Opacity, ZIndex = ZIndex,
    };
}

class ArrowObject : ShapeObject
{
    public override void Render(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = CreatePen();
        pen.CustomEndCap = new AdjustableArrowCap(Thickness + 2, Thickness + 3, true);
        g.DrawLine(pen, Start, End);
    }

    public override bool HitTest(PointF point, float tolerance)
    {
        return PenStroke.DistanceToSegmentStatic(point, Start, End) < Math.Max(tolerance, Thickness / 2 + 6);
    }

    public override AnnotationObject Clone() => new ArrowObject
    {
        Start = Start, End = End, StrokeColor = StrokeColor,
        Thickness = Thickness, Opacity = Opacity, ZIndex = ZIndex,
    };
}

class RectangleObject : ShapeObject
{
    public bool Filled { get; set; }

    public override void Render(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = GetShapeRect();
        if (Filled && FillColor != Color.Transparent)
        {
            using var fillBrush = new SolidBrush(FillColor);
            g.FillRectangle(fillBrush, rect);
        }
        using var pen = CreatePen();
        g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
    }

    public override bool HitTest(PointF point, float tolerance)
    {
        var rect = GetShapeRect();
        if (Filled) return rect.Contains(point);
        rect.Inflate(tolerance, tolerance);
        if (!rect.Contains(point)) return false;
        var inner = GetShapeRect();
        inner.Inflate(-tolerance, -tolerance);
        return !inner.Contains(point);
    }

    public override AnnotationObject Clone() => new RectangleObject
    {
        Start = Start, End = End, StrokeColor = StrokeColor, FillColor = FillColor,
        Thickness = Thickness, Opacity = Opacity, ZIndex = ZIndex, Filled = Filled,
    };
}

class EllipseObject : ShapeObject
{
    public bool Filled { get; set; }

    public override void Render(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = GetShapeRect();
        if (Filled && FillColor != Color.Transparent)
        {
            using var fillBrush = new SolidBrush(FillColor);
            g.FillEllipse(fillBrush, rect);
        }
        using var pen = CreatePen();
        g.DrawEllipse(pen, rect);
    }

    public override bool HitTest(PointF point, float tolerance)
    {
        var rect = GetShapeRect();
        using var path = new GraphicsPath();
        if (Filled)
        {
            path.AddEllipse(rect);
            return path.IsVisible(point);
        }
        // Check outer boundary
        rect.Inflate(tolerance, tolerance);
        path.AddEllipse(rect);
        if (!path.IsVisible(point)) return false;
        // Check inner boundary
        var inner = GetShapeRect();
        inner.Inflate(-tolerance, -tolerance);
        if (inner.Width <= 0 || inner.Height <= 0) return true;
        path.Reset();
        path.AddEllipse(inner);
        return !path.IsVisible(point);
    }

    public override AnnotationObject Clone() => new EllipseObject
    {
        Start = Start, End = End, StrokeColor = StrokeColor, FillColor = FillColor,
        Thickness = Thickness, Opacity = Opacity, ZIndex = ZIndex, Filled = Filled,
    };
}
