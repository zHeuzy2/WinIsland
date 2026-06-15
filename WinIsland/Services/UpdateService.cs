using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace WinIsland.Services;

/// <summary>
/// Checks GitHub Releases for a newer version and, when the user opts in,
/// downloads the published installer and launches it.
///
/// The release pipeline (.github/workflows/release.yml) tags builds as
/// <c>vX.Y.Z</c> and uploads <c>WinIsland-X.Y.Z-Setup.exe</c> to the GitHub
/// Release. This service mirrors that contract: it reads the latest release,
/// compares <see cref="CurrentVersion"/> against the tag, and picks the
/// <c>*-Setup.exe</c> asset to install.
/// </summary>
public sealed class UpdateService
{
    private const string Owner = "zHeuzy";
    private const string Repo = "WinIsland";
    private const string LatestReleaseUrl =
        "https://api.github.com/repos/" + Owner + "/" + Repo + "/releases/latest";

    // GitHub requires a User-Agent on every API request.
    private static readonly HttpClient Http = CreateClient();

    /// <summary>Details about a release that is newer than the running build.</summary>
    public sealed record UpdateInfo(
        Version Version, string Tag, string DownloadUrl, string AssetName, string ReleaseUrl);

    public Version CurrentVersion { get; }

    public UpdateService() => CurrentVersion = ResolveCurrentVersion();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WinIsland-Updater");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>
    /// Queries the latest GitHub release. Returns the update details when a newer
    /// version with a downloadable installer exists, or <c>null</c> otherwise
    /// (already up to date, no network, no matching asset, etc.). Never throws.
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await Http.GetAsync(LatestReleaseUrl, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;

            // Ignore drafts/prereleases for the auto-update path.
            if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
            if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;

            string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            if (!TryParseVersion(tag, out var latest)) return null;
            if (latest <= CurrentVersion) return null;

            // Prefer the installer asset; fall back to the portable zip.
            string? url = null, asset = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    string name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    string link = a.TryGetProperty("browser_download_url", out var d) ? d.GetString() ?? "" : "";
                    if (name.Length == 0 || link.Length == 0) continue;

                    if (name.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        url = link; asset = name; break; // best match
                    }
                    if (url == null && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        url = link; asset = name; // remember, keep looking for -Setup.exe
                    }
                }
            }

            if (url == null || asset == null) return null;

            string htmlUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
            return new UpdateInfo(latest, tag, url, asset, htmlUrl);
        }
        catch
        {
            return null; // offline or unexpected payload: stay silent
        }
    }

    /// <summary>
    /// Downloads the installer to a temp folder and returns its path. Reports a
    /// 0..1 progress fraction when the server provides a content length.
    /// Throws on failure so the caller can surface an error.
    /// </summary>
    public async Task<string> DownloadAsync(
        UpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        string dir = Path.Combine(Path.GetTempPath(), "WinIslandUpdate");
        Directory.CreateDirectory(dir);
        string dest = Path.Combine(dir, info.AssetName);

        using var resp = await Http.GetAsync(
            info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        long? total = resp.Content.Headers.ContentLength;
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = new FileStream(
            dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);

        var buffer = new byte[1 << 16];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            read += n;
            if (total is > 0) progress?.Report((double)read / total.Value);
        }

        return dest;
    }

    /// <summary>
    /// Launches the downloaded installer (elevation is requested by the
    /// installer's own manifest) and signals the caller to quit so the running
    /// files aren't locked during the upgrade.
    /// </summary>
    public static void LaunchInstaller(string installerPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = installerPath,
            UseShellExecute = true, // honour the installer's UAC manifest
            // Skip the "select install folder" steps but keep a progress UI.
            Arguments = "/SILENT /SUPPRESSMSGBOXES /NOCANCEL",
        };
        Process.Start(psi);
    }

    /// <summary>
    /// Resolves the running build's version, preferring the informational
    /// version stamped by the release workflow.
    /// </summary>
    private static Version ResolveCurrentVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (TryParseVersion(info, out var v)) return v;

        return asm.GetName().Version ?? new Version(0, 0, 0);
    }

    /// <summary>Parses "v1.2.3", "1.2.3" or "1.2.3+build" into a 3-part version.</summary>
    private static bool TryParseVersion(string? raw, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(raw)) return false;

        string s = raw.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];

        // Drop any +metadata or -prerelease suffix and keep the numeric core.
        int cut = s.IndexOfAny(new[] { '+', '-', ' ' });
        if (cut >= 0) s = s[..cut];

        var parts = s.Split('.');
        if (parts.Length == 0) return false;

        int major = 0, minor = 0, patch = 0;
        if (!int.TryParse(parts[0], out major)) return false;
        if (parts.Length > 1) int.TryParse(parts[1], out minor);
        if (parts.Length > 2) int.TryParse(parts[2], out patch);

        version = new Version(major, minor, patch);
        return true;
    }
}
