namespace ProdToy.Sdk;

/// <summary>
/// Theme colors exposed to plugins. Stable subset of the host's internal PopupTheme.
/// </summary>
public sealed record PluginTheme(
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
    Color ErrorColor);
