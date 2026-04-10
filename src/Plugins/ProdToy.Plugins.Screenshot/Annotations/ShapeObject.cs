using System.Drawing;
using System.Drawing.Drawing2D;

namespace ProdToy.Plugins.Screenshot;

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
        var state = ApplyRotation(g);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = CreatePen();
        g.DrawLine(pen, Start, End);
        g.Restore(state);
    }

    public override bool HitTest(PointF point, float tolerance)
    {
        var local = RotatePoint(point, GetCenter(), -Rotation);
        return PenStroke.DistanceToSegmentStatic(local, Start, End) < Math.Max(tolerance, Thickness / 2 + 4);
    }

    public override AnnotationObject Clone() => new LineObject
    {
        Start = Start, End = End, StrokeColor = StrokeColor,
        Thickness = Thickness, Opacity = Opacity, ZIndex = ZIndex, Rotation = Rotation,
    };
}

class ArrowObject : ShapeObject
{
    public override void Render(Graphics g)
    {
        var state = ApplyRotation(g);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = CreatePen();
        pen.CustomEndCap = new AdjustableArrowCap(Thickness + 2, Thickness + 3, true);
        g.DrawLine(pen, Start, End);
        g.Restore(state);
    }

    public override bool HitTest(PointF point, float tolerance)
    {
        var local = RotatePoint(point, GetCenter(), -Rotation);
        return PenStroke.DistanceToSegmentStatic(local, Start, End) < Math.Max(tolerance, Thickness / 2 + 6);
    }

    public override AnnotationObject Clone() => new ArrowObject
    {
        Start = Start, End = End, StrokeColor = StrokeColor,
        Thickness = Thickness, Opacity = Opacity, ZIndex = ZIndex, Rotation = Rotation,
    };
}

class RectangleObject : ShapeObject
{
    public bool Filled { get; set; }

    public override void Render(Graphics g)
    {
        var state = ApplyRotation(g);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = GetShapeRect();
        if (Filled && FillColor != Color.Transparent)
        {
            using var fillBrush = new SolidBrush(FillColor);
            g.FillRectangle(fillBrush, rect);
        }
        using var pen = CreatePen();
        g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
        g.Restore(state);
    }

    public override bool HitTest(PointF point, float tolerance)
    {
        var local = RotatePoint(point, GetCenter(), -Rotation);
        var rect = GetShapeRect();
        if (Filled) return rect.Contains(local);
        rect.Inflate(tolerance, tolerance);
        if (!rect.Contains(local)) return false;
        var inner = GetShapeRect();
        inner.Inflate(-tolerance, -tolerance);
        return !inner.Contains(local);
    }

    public override AnnotationObject Clone() => new RectangleObject
    {
        Start = Start, End = End, StrokeColor = StrokeColor, FillColor = FillColor,
        Thickness = Thickness, Opacity = Opacity, ZIndex = ZIndex, Filled = Filled, Rotation = Rotation,
    };
}

class EllipseObject : ShapeObject
{
    public bool Filled { get; set; }

    public override void Render(Graphics g)
    {
        var state = ApplyRotation(g);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = GetShapeRect();
        if (Filled && FillColor != Color.Transparent)
        {
            using var fillBrush = new SolidBrush(FillColor);
            g.FillEllipse(fillBrush, rect);
        }
        using var pen = CreatePen();
        g.DrawEllipse(pen, rect);
        g.Restore(state);
    }

    public override bool HitTest(PointF point, float tolerance)
    {
        var local = RotatePoint(point, GetCenter(), -Rotation);
        var rect = GetShapeRect();
        using var path = new GraphicsPath();
        if (Filled)
        {
            path.AddEllipse(rect);
            return path.IsVisible(local);
        }
        rect.Inflate(tolerance, tolerance);
        path.AddEllipse(rect);
        if (!path.IsVisible(local)) return false;
        var inner = GetShapeRect();
        inner.Inflate(-tolerance, -tolerance);
        if (inner.Width <= 0 || inner.Height <= 0) return true;
        path.Reset();
        path.AddEllipse(inner);
        return !path.IsVisible(local);
    }

    public override AnnotationObject Clone() => new EllipseObject
    {
        Start = Start, End = End, StrokeColor = StrokeColor, FillColor = FillColor,
        Thickness = Thickness, Opacity = Opacity, ZIndex = ZIndex, Filled = Filled, Rotation = Rotation,
    };
}

class MaskBoxObject : ShapeObject
{
    public override void Render(Graphics g)
    {
        var state = ApplyRotation(g);
        var rect = GetShapeRect();
        using var brush = new SolidBrush(StrokeColor);
        g.FillRectangle(brush, rect);

        // Draw repeating password mask symbols (●) inside the box
        if (rect.Width > 8 && rect.Height > 8)
        {
            // Pick a contrasting color for the dots
            int brightness = (StrokeColor.R * 299 + StrokeColor.G * 587 + StrokeColor.B * 114) / 1000;
            var dotColor = Color.FromArgb(80, brightness > 128 ? 0 : 255, brightness > 128 ? 0 : 255, brightness > 128 ? 0 : 255);

            float fontSize = Math.Max(8f, rect.Height * 0.9f);
            using var font = new Font("Segoe UI", fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
            using var dotBrush = new SolidBrush(dotColor);

            // Draw asterisks as 3 crossed lines (truly centered, font-independent)
            float starSize = rect.Height * 0.35f;
            float spacing = starSize * 2.2f;
            float cy = rect.Y + rect.Height / 2f;
            using var starPen = new Pen(Color.FromArgb(80, 255, 255, 255), Math.Max(1.5f, starSize * 0.18f));
            starPen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
            starPen.EndCap = System.Drawing.Drawing2D.LineCap.Round;

            for (float cx = rect.X + spacing * 0.5f; cx < rect.Right; cx += spacing)
            {
                // Vertical line
                g.DrawLine(starPen, cx, cy - starSize, cx, cy + starSize);
                // Diagonal lines (60° rotated)
                float dx60 = starSize * 0.866f; // cos(30°)
                float dy60 = starSize * 0.5f;   // sin(30°)
                g.DrawLine(starPen, cx - dx60, cy - dy60, cx + dx60, cy + dy60);
                g.DrawLine(starPen, cx - dx60, cy + dy60, cx + dx60, cy - dy60);
            }
        }
        g.Restore(state);
    }

    public override bool HitTest(PointF point, float tolerance)
    {
        var local = RotatePoint(point, GetCenter(), -Rotation);
        var rect = GetShapeRect();
        rect.Inflate(tolerance, tolerance);
        return rect.Contains(local);
    }

    public override AnnotationObject Clone() => new MaskBoxObject
    {
        Start = Start, End = End, StrokeColor = StrokeColor,
        Thickness = Thickness, Opacity = Opacity, ZIndex = ZIndex, Rotation = Rotation,
    };
}
