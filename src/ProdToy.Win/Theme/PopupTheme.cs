using System.Drawing;

namespace ProdToy;

record PopupTheme(
    string Name,
    Color BgDark,
    Color BgHeader,
    Color Primary,
    Color PrimaryLight,
    Color PrimaryDim,
    Color TextPrimary,
    Color TextSecondary,
    Color Border,
    Color SuccessColor,
    Color SuccessBg,
    Color ErrorColor,
    Color ErrorBg
);

static class Themes
{
    public static readonly PopupTheme[] All =
    [
        new("Ocean Blue",
            BgDark: Color.FromArgb(15, 20, 30),
            BgHeader: Color.FromArgb(12, 16, 26),
            Primary: Color.FromArgb(56, 132, 244),
            PrimaryLight: Color.FromArgb(96, 165, 250),
            PrimaryDim: Color.FromArgb(30, 58, 110),
            TextPrimary: Color.FromArgb(235, 240, 255),
            TextSecondary: Color.FromArgb(160, 175, 210),
            Border: Color.FromArgb(40, 55, 85),
            SuccessColor: Color.FromArgb(52, 211, 153),
            SuccessBg: Color.FromArgb(16, 52, 40),
            ErrorColor: Color.FromArgb(248, 113, 113),
            ErrorBg: Color.FromArgb(60, 20, 20)
        ),

        new("Deep Purple",
            BgDark: Color.FromArgb(18, 14, 30),
            BgHeader: Color.FromArgb(14, 10, 24),
            Primary: Color.FromArgb(139, 92, 246),
            PrimaryLight: Color.FromArgb(167, 139, 250),
            PrimaryDim: Color.FromArgb(55, 30, 110),
            TextPrimary: Color.FromArgb(240, 235, 255),
            TextSecondary: Color.FromArgb(175, 160, 210),
            Border: Color.FromArgb(55, 40, 85),
            SuccessColor: Color.FromArgb(52, 211, 153),
            SuccessBg: Color.FromArgb(16, 52, 40),
            ErrorColor: Color.FromArgb(248, 113, 113),
            ErrorBg: Color.FromArgb(60, 20, 20)
        ),

        new("Emerald",
            BgDark: Color.FromArgb(12, 22, 18),
            BgHeader: Color.FromArgb(8, 18, 14),
            Primary: Color.FromArgb(16, 185, 129),
            PrimaryLight: Color.FromArgb(52, 211, 153),
            PrimaryDim: Color.FromArgb(12, 60, 45),
            TextPrimary: Color.FromArgb(230, 255, 245),
            TextSecondary: Color.FromArgb(150, 210, 185),
            Border: Color.FromArgb(30, 65, 50),
            SuccessColor: Color.FromArgb(52, 211, 153),
            SuccessBg: Color.FromArgb(16, 52, 40),
            ErrorColor: Color.FromArgb(248, 113, 113),
            ErrorBg: Color.FromArgb(60, 20, 20)
        ),

        new("Amber",
            BgDark: Color.FromArgb(24, 18, 10),
            BgHeader: Color.FromArgb(20, 14, 6),
            Primary: Color.FromArgb(245, 158, 11),
            PrimaryLight: Color.FromArgb(251, 191, 36),
            PrimaryDim: Color.FromArgb(80, 50, 10),
            TextPrimary: Color.FromArgb(255, 248, 230),
            TextSecondary: Color.FromArgb(210, 180, 140),
            Border: Color.FromArgb(75, 55, 25),
            SuccessColor: Color.FromArgb(52, 211, 153),
            SuccessBg: Color.FromArgb(16, 52, 40),
            ErrorColor: Color.FromArgb(248, 113, 113),
            ErrorBg: Color.FromArgb(60, 20, 20)
        ),

        new("Rose",
            BgDark: Color.FromArgb(25, 14, 18),
            BgHeader: Color.FromArgb(20, 10, 14),
            Primary: Color.FromArgb(244, 63, 94),
            PrimaryLight: Color.FromArgb(251, 113, 133),
            PrimaryDim: Color.FromArgb(80, 20, 35),
            TextPrimary: Color.FromArgb(255, 235, 240),
            TextSecondary: Color.FromArgb(210, 160, 175),
            Border: Color.FromArgb(75, 35, 45),
            SuccessColor: Color.FromArgb(52, 211, 153),
            SuccessBg: Color.FromArgb(16, 52, 40),
            ErrorColor: Color.FromArgb(248, 113, 113),
            ErrorBg: Color.FromArgb(60, 20, 20)
        ),

        new("Cyberpunk",
            BgDark: Color.FromArgb(10, 10, 20),
            BgHeader: Color.FromArgb(6, 6, 16),
            Primary: Color.FromArgb(0, 255, 200),
            PrimaryLight: Color.FromArgb(80, 255, 220),
            PrimaryDim: Color.FromArgb(0, 60, 48),
            TextPrimary: Color.FromArgb(220, 255, 248),
            TextSecondary: Color.FromArgb(130, 200, 180),
            Border: Color.FromArgb(20, 60, 50),
            SuccessColor: Color.FromArgb(0, 255, 200),
            SuccessBg: Color.FromArgb(0, 40, 32),
            ErrorColor: Color.FromArgb(255, 60, 100),
            ErrorBg: Color.FromArgb(60, 10, 20)
        ),

        new("Mono",
            BgDark: Color.FromArgb(0, 0, 0),
            BgHeader: Color.FromArgb(10, 10, 10),
            Primary: Color.FromArgb(200, 200, 200),
            PrimaryLight: Color.FromArgb(240, 240, 240),
            PrimaryDim: Color.FromArgb(40, 40, 40),
            TextPrimary: Color.FromArgb(245, 245, 245),
            TextSecondary: Color.FromArgb(160, 160, 160),
            Border: Color.FromArgb(50, 50, 50),
            SuccessColor: Color.FromArgb(200, 200, 200),
            SuccessBg: Color.FromArgb(25, 25, 25),
            ErrorColor: Color.FromArgb(255, 100, 100),
            ErrorBg: Color.FromArgb(40, 10, 10)
        ),

        new("Slate Blue",
            BgDark: Color.FromArgb(42, 50, 62),
            BgHeader: Color.FromArgb(36, 43, 55),
            Primary: Color.FromArgb(100, 160, 255),
            PrimaryLight: Color.FromArgb(140, 185, 255),
            PrimaryDim: Color.FromArgb(55, 72, 100),
            TextPrimary: Color.FromArgb(225, 232, 242),
            TextSecondary: Color.FromArgb(160, 172, 192),
            Border: Color.FromArgb(62, 72, 88),
            SuccessColor: Color.FromArgb(72, 210, 150),
            SuccessBg: Color.FromArgb(38, 62, 52),
            ErrorColor: Color.FromArgb(248, 120, 120),
            ErrorBg: Color.FromArgb(72, 40, 40)
        ),

        new("Warm Gray",
            BgDark: Color.FromArgb(55, 50, 48),
            BgHeader: Color.FromArgb(48, 43, 41),
            Primary: Color.FromArgb(230, 160, 80),
            PrimaryLight: Color.FromArgb(245, 185, 110),
            PrimaryDim: Color.FromArgb(80, 62, 45),
            TextPrimary: Color.FromArgb(235, 228, 220),
            TextSecondary: Color.FromArgb(180, 168, 155),
            Border: Color.FromArgb(75, 68, 62),
            SuccessColor: Color.FromArgb(120, 200, 140),
            SuccessBg: Color.FromArgb(48, 62, 48),
            ErrorColor: Color.FromArgb(240, 110, 100),
            ErrorBg: Color.FromArgb(72, 42, 38)
        ),

        new("Sage",
            BgDark: Color.FromArgb(45, 52, 48),
            BgHeader: Color.FromArgb(38, 46, 42),
            Primary: Color.FromArgb(110, 190, 150),
            PrimaryLight: Color.FromArgb(145, 215, 175),
            PrimaryDim: Color.FromArgb(55, 75, 62),
            TextPrimary: Color.FromArgb(225, 238, 230),
            TextSecondary: Color.FromArgb(158, 178, 168),
            Border: Color.FromArgb(62, 75, 68),
            SuccessColor: Color.FromArgb(110, 210, 160),
            SuccessBg: Color.FromArgb(42, 62, 50),
            ErrorColor: Color.FromArgb(240, 115, 110),
            ErrorBg: Color.FromArgb(70, 42, 40)
        ),

        new("Dusk",
            BgDark: Color.FromArgb(50, 45, 58),
            BgHeader: Color.FromArgb(43, 38, 52),
            Primary: Color.FromArgb(170, 130, 220),
            PrimaryLight: Color.FromArgb(195, 160, 240),
            PrimaryDim: Color.FromArgb(68, 55, 85),
            TextPrimary: Color.FromArgb(232, 225, 242),
            TextSecondary: Color.FromArgb(172, 162, 190),
            Border: Color.FromArgb(70, 62, 82),
            SuccessColor: Color.FromArgb(120, 205, 160),
            SuccessBg: Color.FromArgb(44, 58, 50),
            ErrorColor: Color.FromArgb(240, 115, 120),
            ErrorBg: Color.FromArgb(70, 40, 42)
        ),

        new("Lite",
            BgDark: Color.FromArgb(245, 245, 248),
            BgHeader: Color.FromArgb(250, 251, 254),
            Primary: Color.FromArgb(56, 132, 244),
            PrimaryLight: Color.FromArgb(96, 165, 250),
            PrimaryDim: Color.FromArgb(210, 225, 245),
            TextPrimary: Color.FromArgb(50, 60, 80),
            TextSecondary: Color.FromArgb(110, 120, 140),
            Border: Color.FromArgb(210, 215, 225),
            SuccessColor: Color.FromArgb(22, 163, 74),
            SuccessBg: Color.FromArgb(220, 252, 231),
            ErrorColor: Color.FromArgb(220, 38, 38),
            ErrorBg: Color.FromArgb(254, 226, 226)
        ),
    ];

    public static PopupTheme Default => All.First(t => t.Name == "Warm Gray");

    public static PopupTheme LoadSaved()
    {
        var settings = AppSettings.Load();
        return All.FirstOrDefault(t => t.Name == settings.Theme) ?? Default;
    }

    public static void Save(PopupTheme theme)
    {
        var settings = AppSettings.Load();
        AppSettings.Save(settings with { Theme = theme.Name });
    }

    /// <summary>
    /// Create a simple colored icon programmatically for the tray and window.
    /// </summary>
    public static Icon CreateAppIcon(Color primary)
    {
        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        // Filled circle with primary color
        using var brush = new SolidBrush(primary);
        g.FillEllipse(brush, 2, 2, 28, 28);

        // "C" letter in white
        using var font = new Font("Segoe UI", 16f, FontStyle.Bold);
        using var textBrush = new SolidBrush(Color.White);
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString("D", font, textBrush, new RectangleF(0, 0, 32, 32), sf);

        // Outer glow ring
        using var pen = new Pen(Color.FromArgb(100, primary), 1.5f);
        g.DrawEllipse(pen, 1, 1, 30, 30);

        return Icon.FromHandle(bmp.GetHicon());
    }
}
