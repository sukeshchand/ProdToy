namespace ProdToy.Plugins.ClaudeIntegration;

/// <summary>
/// Notification type string constants. Plugin-local mirror of the host's
/// NotificationType so ChatHistory can write entries without reaching into
/// host internals. Strings must match the host's values on the wire.
/// </summary>
static class NotificationType
{
    public const string Info = "info";
    public const string Success = "success";
    public const string Error = "error";
    public const string Pending = "pending";
}
