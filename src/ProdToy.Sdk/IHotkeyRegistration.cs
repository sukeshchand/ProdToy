namespace ProdToy.Sdk;

/// <summary>
/// Handle for a registered global hotkey. Dispose or call Unregister to release.
/// </summary>
public interface IHotkeyRegistration : IDisposable
{
    void Unregister();
}
