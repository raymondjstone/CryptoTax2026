using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace CryptoTax2026.Services;

public class UpdateCheckService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/raymondjstone/CryptoTax2026/releases/latest";
    private const string ReleasesPageUrl = "https://github.com/raymondjstone/CryptoTax2026/releases/latest";

    private static readonly HttpClient _http = new();

    static UpdateCheckService()
    {
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CryptoTax2026", "1.0"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <summary>
    /// Gets the current app version from the assembly metadata.
    /// </summary>
    public static Version GetCurrentVersion()
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        return asm.GetName().Version ?? new Version(0, 0, 0, 0);
    }

    /// <summary>
    /// Checks GitHub for the latest release. Returns the newer version and download URL
    /// if an update is available, or null if the app is up to date.
    /// </summary>
    public static async Task<(Version NewVersion, string DownloadUrl)?> CheckForUpdateAsync()
    {
        try
        {
            var response = await _http.GetAsync(LatestReleaseUrl);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("tag_name", out var tagElement))
                return null;

            var tag = tagElement.GetString();
            if (string.IsNullOrEmpty(tag))
                return null;

            // Tags are formatted as "v1.0.22.0" — strip the leading 'v'
            var versionStr = tag.StartsWith('v') ? tag[1..] : tag;
            if (!Version.TryParse(versionStr, out var latestVersion))
                return null;

            var current = GetCurrentVersion();

            if (latestVersion > current)
                return (latestVersion, ReleasesPageUrl);

            return null;
        }
        catch
        {
            // Network errors, JSON parse failures, etc. — silently ignore
            return null;
        }
    }
}
