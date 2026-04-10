namespace ProdToy.Sdk;

/// <summary>
/// A plugin's contribution to the Settings dialog.
/// The host calls CreateContent when the settings tab is opened.
/// </summary>
public sealed record SettingsPageContribution(
    string TabTitle,
    Func<Control> CreateContent,
    int TabOrder = 500);
