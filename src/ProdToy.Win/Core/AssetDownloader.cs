using System.Diagnostics;

namespace ProdToy;

/// <summary>
/// Downloads release artifacts (zips) for the updater and the plugin store.
/// Resolves relative asset paths from metadata.json against the manifest URL,
/// trying sibling-directory layout first (matches local deploys) and then a
/// flat layout (matches GitHub Releases, which is a flat asset namespace).
/// </summary>
static class AssetDownloader
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(5),
    };

    static AssetDownloader()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ProdToy/" + AppVersion.Current);
    }

    /// <summary>
    /// Download a relative asset into a temp file and return its path.
    /// Caller is responsible for deleting the temp file when done.
    /// Tries "{manifestDir}/{relPath}" first, then "{manifestDir}/{basename(relPath)}".
    /// If expectedSha256 is non-empty, the downloaded file is hashed and compared
    /// case-insensitively; mismatch deletes the file and throws.
    /// Throws if both attempts fail.
    /// </summary>
    public static async Task<string> DownloadRelativeAssetAsync(
        string manifestUrl, string relPath, string expectedSha256 = "")
    {
        string baseDir = GetDirectoryUrl(manifestUrl);
        string cleanedRel = relPath.TrimStart('/', '\\').Replace('\\', '/');
        string siblingUrl = baseDir + "/" + cleanedRel;
        string flatUrl = baseDir + "/" + Path.GetFileName(cleanedRel);

        // Flat-first: GitHub Releases (the default update host) uses a flat
        // asset namespace, so the sibling-dir URL would 404 there. Sibling
        // stays as a fallback for HTTP mirrors that replicate the local layout.
        var attempts = siblingUrl == flatUrl
            ? new[] { flatUrl }
            : new[] { flatUrl, siblingUrl };

        Log.Info($"AssetDownloader: {relPath} → {attempts.Length} attempt(s): {string.Join(" | ", attempts)}");

        Exception? lastError = null;
        foreach (var url in attempts)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                Log.Info($"GET {url}");
                using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                Log.Info($"  status={(int)resp.StatusCode} {resp.StatusCode} len={resp.Content.Headers.ContentLength?.ToString() ?? "?"} in {sw.ElapsedMilliseconds}ms");
                resp.EnsureSuccessStatusCode();

                string tempFile = Path.Combine(Path.GetTempPath(),
                    $"prodtoy_{Guid.NewGuid():N}_{Path.GetFileName(cleanedRel)}");
                using (var fs = File.Create(tempFile))
                {
                    await resp.Content.CopyToAsync(fs).ConfigureAwait(false);
                }
                var info = new FileInfo(tempFile);
                Log.Info($"  saved {info.Length} bytes to {tempFile} in {sw.ElapsedMilliseconds}ms total");

                if (!string.IsNullOrWhiteSpace(expectedSha256))
                {
                    string actual = ComputeSha256(tempFile);
                    if (!actual.Equals(expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(tempFile); } catch { }
                        string msg = $"SHA256 mismatch for {Path.GetFileName(cleanedRel)}: " +
                                     $"expected {expectedSha256.Trim()}, got {actual}";
                        Log.Error(msg);
                        throw new InvalidOperationException(msg);
                    }
                    Log.Info($"  sha256 verified ({actual.Substring(0, 12)}...)");
                }
                return tempFile;
            }
            catch (Exception ex)
            {
                lastError = ex;
                Log.Warn($"GET {url} failed after {sw.ElapsedMilliseconds}ms: {ex.Message}");
            }
        }

        Log.Error($"All attempts failed for {relPath}", lastError);
        throw new InvalidOperationException(
            $"Failed to download {relPath}. Tried: {string.Join(", ", attempts)}",
            lastError);
    }

    /// <summary>
    /// Compute SHA256 of a file and return lowercase hex. Used to verify
    /// downloaded zips against the hash advertised in metadata.json.
    /// </summary>
    public static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Strip the trailing filename component off a URL, keeping the scheme.</summary>
    private static string GetDirectoryUrl(string url)
    {
        int lastSlash = url.LastIndexOf('/');
        // Don't chop past "https://"
        if (lastSlash < 0 || lastSlash < url.IndexOf("://", StringComparison.Ordinal) + 3)
            return url;
        return url.Substring(0, lastSlash);
    }
}
