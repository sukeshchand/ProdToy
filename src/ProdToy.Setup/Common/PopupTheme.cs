using System.Drawing;

namespace ProdToy.Setup;

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

/// <summary>
/// Trimmed theme palette for the installer. Uses a single fixed theme so the
/// installer has no dependency on the host's settings.json.
/// </summary>
static class Themes
{
    public static readonly PopupTheme Default = new(
        Name: "Warm Gray",
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
    );
}
