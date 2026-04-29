using System.Drawing;
using System.Drawing.Drawing2D;

namespace ProdToy.Plugins.Screenshot;

class TextObject : AnnotationObject
{
    public string Text { get; set; } = "";
    public PointF Position { get; set; }
    public float FontSize { get; set; } = 16f;
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    public string FontFamily { get; set; } = "Segoe UI";
    public bool IsEditing { get; set; }

    /// <summary>Insertion index inside <see cref="Text"/>. Always clamped to
    /// [0, Text.Length] by the canvas before render. Only meaningful while
    /// <see cref="IsEditing"/> is true.</summary>
    public int CaretIndex { get; set; }

    /// <summary>Driven by the canvas blink timer. Render skips the caret when
    /// false so it visibly blinks instead of looking like a thin pen mark.</summary>
    public bool CaretVisible { get; set; } = true;

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

        if (IsEditing && CaretVisible)
        {
            // Compute caret pixel position from CaretIndex with multi-line
            // support. Split into lines by '\n'; the line index is the number
            // of '\n' chars before CaretIndex, and the column is the substring
            // from the start of that line to the caret.
            int caret = Math.Clamp(CaretIndex, 0, Text.Length);
            string before = Text.Substring(0, caret);
            int lineIndex = 0;
            int lineStart = 0;
            for (int i = 0; i < before.Length; i++)
            {
                if (before[i] == '\n') { lineIndex++; lineStart = i + 1; }
            }
            string lineUpToCaret = before.Substring(lineStart);
            float colWidth = string.IsNullOrEmpty(lineUpToCaret)
                ? 0f
                : MeasureSubstring(g, font, lineUpToCaret);
            float lineHeight = font.GetHeight(g);
            float cursorX = Position.X + colWidth;
            float cursorTop = Position.Y + lineIndex * lineHeight + 2;
            float cursorBottom = Position.Y + (lineIndex + 1) * lineHeight - 2;
            using var cursorPen = new Pen(StrokeColor, 1.5f);
            g.DrawLine(cursorPen, cursorX, cursorTop, cursorX, cursorBottom);
        }
        g.Restore(state);
    }

    /// <summary>Measure a non-empty substring using the same renderer settings
    /// the editing caret needs. Trailing-space trimming is the common GDI+
    /// trap; we use a wide bounding rect + GenericTypographic to avoid it.</summary>
    private static float MeasureSubstring(Graphics g, Font font, string s)
    {
        var fmt = System.Drawing.StringFormat.GenericTypographic;
        var size = g.MeasureString(s, font, new SizeF(10000, 10000), fmt);
        return size.Width;
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
        Text = Text, Position = Position, FontSize = FontSize,
        Bold = Bold, Italic = Italic, Underline = Underline, FontFamily = FontFamily,
        StrokeColor = StrokeColor, Opacity = Opacity, ZIndex = ZIndex, Rotation = Rotation,
        CaretIndex = CaretIndex,
    };

    private Font GetFont()
    {
        var style = FontStyle.Regular;
        if (Bold) style |= FontStyle.Bold;
        if (Italic) style |= FontStyle.Italic;
        if (Underline) style |= FontStyle.Underline;
        return FontPool.Get(string.IsNullOrEmpty(FontFamily) ? "Segoe UI" : FontFamily, FontSize, style);
    }

    private SizeF MeasureText(Font font)
    {
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        string text = string.IsNullOrEmpty(Text) ? "A" : Text;
        return g.MeasureString(text, font);
    }
}
