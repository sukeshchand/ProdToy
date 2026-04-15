namespace ProdToy.Sdk;

/// <summary>
/// A plugin's contribution to the tray context menu or dashboard tile grid.
///
/// <para><see cref="Icon"/> is a short string (typically a single emoji or
/// Segoe MDL2 Assets glyph) used by the dashboard to render the tile icon.
/// Plugins supply this directly instead of the host guessing from Text, so
/// the host stays plugin-agnostic. The tray menu currently ignores it.</para>
/// </summary>
public sealed record MenuContribution(
    string Text,
    Action OnClick,
    int Priority = 500,
    bool Visible = true,
    bool IsSeparatorBefore = false,
    string Icon = "\uD83D\uDD0C");
