using ProdToy.Sdk;

namespace ProdToy;

/// <summary>
/// Runtime state for a loaded or discovered plugin.
/// </summary>
sealed class PluginInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required string Description { get; init; }
    public required string Author { get; init; }
    public required string DllPath { get; init; }
    public required string PluginDirectory { get; init; }
    public bool Enabled { get; set; }
    public IPlugin? Instance { get; set; }
    public PluginLoadContext? LoadContext { get; set; }
    public PluginAttribute? Metadata { get; set; }
    public string? LoadError { get; set; }
}
