namespace ProdToy.Sdk;

/// <summary>
/// Metadata annotation placed on the plugin class.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class PluginAttribute : Attribute
{
    public string Id { get; }
    public string Name { get; }
    public string Version { get; }
    public string Description { get; set; } = "";
    public string Author { get; set; } = "";

    /// <summary>
    /// Priority for menu ordering. Lower values appear first.
    /// Host items use 0–99. Plugins should use 100+.
    /// </summary>
    public int MenuPriority { get; set; } = 500;

    /// <summary>
    /// Comma-separated list of plugin IDs this plugin depends on.
    /// </summary>
    public string? Dependencies { get; set; }

    public PluginAttribute(string id, string name, string version)
    {
        Id = id;
        Name = name;
        Version = version;
    }
}
