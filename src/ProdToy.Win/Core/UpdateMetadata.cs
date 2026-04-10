using System.Text.Json.Serialization;

namespace ProdToy;

record UpdateMetadata
{
    [JsonPropertyName("version")]
    public string Version { get; init; } = "";

    [JsonPropertyName("releaseNotes")]
    public string ReleaseNotes { get; init; } = "";

    [JsonPropertyName("publishedAt")]
    public string PublishedAt { get; init; } = "";

    /// <summary>
    /// HTTP URL to download the bare exe from (set when update source is a URL).
    /// Null/empty when update source is a local/network path.
    /// </summary>
    [JsonIgnore]
    public string DownloadUrl { get; init; } = "";

    /// <summary>
    /// HTTP URL to download the full bundle zip (exe + plugins).
    /// Preferred over DownloadUrl when available.
    /// </summary>
    [JsonIgnore]
    public string BundleDownloadUrl { get; init; } = "";
}
