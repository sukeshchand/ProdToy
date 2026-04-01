using System.Drawing;
using System.Drawing.Drawing2D;

namespace ProdToy;

/// <summary>
/// An image layer dropped onto the canvas. Renders a bitmap at a position,
/// selectable, movable, and resizable.
/// </summary>
class ImageObject : AnnotationObject, IDisposable
{
    private bool _disposed;
    public Bitmap Image { get; set; } = null!;
    public PointF Position { get; set; }
    public SizeF DisplaySize { get; set; }

    /// <summary>Source file path (for serialization).</summary>
    public string? SourcePath { get; set; }

    public override RectangleF GetBounds()
    {
        return new RectangleF(Position.X - 2, Position.Y - 2, DisplaySize.Width + 4, DisplaySize.Height + 4);
    }

    public override void Render(Graphics g)
    {
        if (Image == null) return;
        var state = ApplyRotation(g);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(Image, Position.X, Position.Y, DisplaySize.Width, DisplaySize.Height);
        g.Restore(state);
    }

    public override bool HitTest(PointF point, float tolerance)
    {
        var local = RotatePoint(point, GetCenter(), -Rotation);
        var bounds = new RectangleF(Position, DisplaySize);
        bounds.Inflate(tolerance, tolerance);
        return bounds.Contains(local);
    }

    public override void Move(float dx, float dy)
    {
        Position = new PointF(Position.X + dx, Position.Y + dy);
    }

    public override void Resize(HandlePosition handle, float dx, float dy)
    {
        var bounds = new RectangleF(Position, DisplaySize);
        switch (handle)
        {
            case HandlePosition.BottomRight:
                DisplaySize = new SizeF(Math.Max(20, DisplaySize.Width + dx), Math.Max(20, DisplaySize.Height + dy));
                break;
            case HandlePosition.MiddleRight:
                DisplaySize = new SizeF(Math.Max(20, DisplaySize.Width + dx), DisplaySize.Height);
                break;
            case HandlePosition.BottomCenter:
                DisplaySize = new SizeF(DisplaySize.Width, Math.Max(20, DisplaySize.Height + dy));
                break;
            case HandlePosition.TopLeft:
                Position = new PointF(Position.X + dx, Position.Y + dy);
                DisplaySize = new SizeF(Math.Max(20, DisplaySize.Width - dx), Math.Max(20, DisplaySize.Height - dy));
                break;
            case HandlePosition.TopCenter:
                Position = new PointF(Position.X, Position.Y + dy);
                DisplaySize = new SizeF(DisplaySize.Width, Math.Max(20, DisplaySize.Height - dy));
                break;
            case HandlePosition.TopRight:
                Position = new PointF(Position.X, Position.Y + dy);
                DisplaySize = new SizeF(Math.Max(20, DisplaySize.Width + dx), Math.Max(20, DisplaySize.Height - dy));
                break;
            case HandlePosition.MiddleLeft:
                Position = new PointF(Position.X + dx, Position.Y);
                DisplaySize = new SizeF(Math.Max(20, DisplaySize.Width - dx), DisplaySize.Height);
                break;
            case HandlePosition.BottomLeft:
                Position = new PointF(Position.X + dx, Position.Y);
                DisplaySize = new SizeF(Math.Max(20, DisplaySize.Width - dx), Math.Max(20, DisplaySize.Height + dy));
                break;
        }
    }

    public override AnnotationObject Clone()
    {
        return new ImageObject
        {
            Image = (Bitmap)Image.Clone(),
            Position = Position,
            DisplaySize = DisplaySize,
            SourcePath = SourcePath,
            StrokeColor = StrokeColor,
            Opacity = Opacity,
            ZIndex = ZIndex,
            Rotation = Rotation,
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Image?.Dispose();
    }
}
