using System.Text.Json.Serialization;

namespace ProdToy;

record CatalogEntry
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("version")] public string Version { get; init; } = "";
    [JsonPropertyName("description")] public string Description { get; init; } = "";
    [JsonPropertyName("author")] public string Author { get; init; } = "";
    [JsonPropertyName("downloadUrl")] public string DownloadUrl { get; init; } = "";
    [JsonPropertyName("minHostVersion")] public string MinHostVersion { get; init; } = "";
    [JsonPropertyName("dependencies")] public string[] Dependencies { get; init; } = [];
    [JsonPropertyName("size")] public long Size { get; init; }
}

record PluginCatalogManifest
{
    [JsonPropertyName("catalogVersion")] public int CatalogVersion { get; init; } = 1;
    [JsonPropertyName("plugins")] public List<CatalogEntry> Plugins { get; init; } = new();
}
