using System.Drawing;
using System.Drawing.Drawing2D;

namespace ProdToy.Plugins.Screenshot;

class TextObject : AnnotationObject
{
    public string Text { get; set; } = "";
    public PointF Position { get; set; }
    public float FontSize { get; set; } = 16f;
    public bool Bold { get; set; }
    public bool IsEditing { get; set; }

    public override RectangleF GetBounds()
    {
        var font = GetFont();
        var size = MeasureText(font);
        return new RectangleF(Position.X - 2, Position.Y - 2, size.Width + 4, size.Height + 4);
    }

    public override void Render(Graphics g)
    {
        if (string.IsNullOrEmpty(Text) && !IsEditing) return;
        var state = ApplyRotation(g);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var font = GetFont();
        using var brush = CreateBrush();

        string displayText = Text;
        if (IsEditing && string.IsNullOrEmpty(displayText))
            displayText = " ";

        g.DrawString(displayText, font, brush, Position);

        if (IsEditing)
        {
            var textSize = g.MeasureString(Text, font);
            float cursorX = Position.X + textSize.Width;
            float cursorTop = Position.Y + 2;
            float cursorBottom = Position.Y + font.GetHeight(g) - 2;
            using var cursorPen = new Pen(StrokeColor, 1.5f);
            g.DrawLine(cursorPen, cursorX, cursorTop, cursorX, cursorBottom);
        }
        g.Restore(state);
    }

    public override bool HitTest(PointF point, float tolerance)
    {
        var local = RotatePoint(point, GetCenter(), -Rotation);
        var bounds = GetBounds();
        bounds.Inflate(tolerance, tolerance);
        return bounds.Contains(local);
    }

    public override void Move(float dx, float dy)
    {
        Position = new PointF(Position.X + dx, Position.Y + dy);
    }

    public override void Resize(HandlePosition handle, float dx, float dy)
    {
        // Text resizing changes font size proportionally
        if (handle is HandlePosition.BottomRight or HandlePosition.MiddleRight or HandlePosition.BottomCenter)
        {
            float scale = 1 + dx / Math.Max(GetBounds().Width, 1);
            FontSize = Math.Clamp(FontSize * scale, 8f, 200f);
        }
    }

    public override AnnotationObject Clone() => new TextObject
    {
        Text = Text, Position = Position, FontSize = FontSize, Bold = Bold,
        StrokeColor = StrokeColor, Opacity = Opacity, ZIndex = ZIndex, Rotation = Rotation,
    };

    private Font GetFont()
    {
        var style = Bold ? FontStyle.Bold : FontStyle.Regular;
        return FontPool.Get("Segoe UI", FontSize, style);
    }

    private SizeF MeasureText(Font font)
    {
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        string text = string.IsNullOrEmpty(Text) ? "A" : Text;
        return g.MeasureString(text, font);
    }
}
