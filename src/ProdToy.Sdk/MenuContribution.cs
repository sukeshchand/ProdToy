namespace ProdToy.Sdk;

/// <summary>
/// A plugin's contribution to the tray context menu.
/// </summary>
public sealed record MenuContribution(
    string Text,
    Action OnClick,
    int Priority = 500,
    bool Visible = true,
    bool IsSeparatorBefore = false);
