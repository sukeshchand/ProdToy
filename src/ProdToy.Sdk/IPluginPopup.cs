namespace ProdToy.Sdk;

/// <summary>
/// Owned by a plugin that wants to manage its own popup window rather than
/// use the generic <see cref="INotificationFacility"/>. Registering via
/// <see cref="IPluginHost.RegisterPopup"/> lets the host forward lifecycle
/// events (theme change, font change, exit) to the popup.
/// </summary>
public interface IPluginPopup : IDisposable
{
    void Show();
    void Hide();
    void BringToFront();
    bool IsVisible { get; }
}
