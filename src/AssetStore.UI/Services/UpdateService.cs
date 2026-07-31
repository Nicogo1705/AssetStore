// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace AssetStore.App.Services;

/// <summary>
/// Checks whether a newer release of the desktop app is available on GitHub, by comparing the running
/// assembly version with the latest release tag. Uses the (User-Agent-configured) api.github.com client
/// shared with <see cref="GitHubAuth"/>; the call is unauthenticated and made at most once per session.
/// </summary>
public sealed class UpdateService(GitHubAuth auth, AppInfo app)
{
    public bool Checked { get; private set; }

    public bool UpdateAvailable { get; private set; }

    /// <summary>True when a newer release exists but this platform's zip isn't uploaded yet
    /// (the release workflow creates the release first, then adds the assets) — updating now
    /// would download nothing, so the UI shows "publishing, retry shortly" instead.</summary>
    public bool Publishing { get; private set; }

    public string CurrentVersion { get; } = CurrentAssemblyVersion();

    public string? LatestVersion { get; private set; }

    /// <summary>Web URL of the latest release (release notes), when known.</summary>
    public string? ReleaseUrl { get; private set; }

    /// <summary>Direct download URL of the build for the current OS/architecture, when known.</summary>
    public string? DownloadUrl { get; private set; }

    /// <summary>Size in bytes of that build's release asset, when known.</summary>
    public long? DownloadSize { get; private set; }

    /// <summary>Queries the latest release once. Safe to call repeatedly (no-ops after the first).</summary>
    public async Task CheckAsync(CancellationToken ct = default)
    {
        if (Checked)
        {
            return;
        }

        Checked = true;
        Publishing = false;

        try
        {
            var (owner, repo) = ParseRepo(app.Repo);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"repos/{owner}/{repo}/releases/latest");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            if (!string.IsNullOrEmpty(auth.Token))
            {
                // Use the signed-in token when available so the check isn't subject to the 60 req/h anon limit.
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
            }
            using var response = await auth.Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return; // no releases yet, rate-limited, or offline — silently skip
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            ReleaseUrl = doc.RootElement.TryGetProperty("html_url", out var h) ? h.GetString() : GitLinks.ReleasesLatest(app.Repo);
            LatestVersion = tag?.TrimStart('v', 'V');

            if (Version.TryParse(CurrentVersion, out var current)
                && Version.TryParse(LatestVersion, out var latest)
                && latest > current)
            {
                var build = DesktopBuilds.Current();
                if (build is not null)
                {
                    // The release is created BEFORE the workflow uploads the zips: only announce the
                    // update once this platform's asset is actually there, otherwise flag "publishing".
                    if (doc.RootElement.TryGetProperty("assets", out var assets))
                    {
                        foreach (var asset in assets.EnumerateArray())
                        {
                            if (asset.TryGetProperty("name", out var n)
                                && string.Equals(n.GetString(), build.AssetName, StringComparison.OrdinalIgnoreCase))
                            {
                                UpdateAvailable = true;
                                DownloadUrl = GitLinks.LatestAssetDownload(app.Repo, build.AssetName);
                                DownloadSize = asset.TryGetProperty("size", out var size) ? size.GetInt64() : null;
                                break;
                            }
                        }
                    }

                    if (!UpdateAvailable)
                    {
                        Publishing = true;
                        Checked = false; // let a later check pick the finished release up
                    }
                }
                else
                {
                    // Unsupported/unknown platform: no zip to wait for, just point at the release page.
                    UpdateAvailable = true;
                    DownloadUrl = GitLinks.ReleasesLatest(app.Repo);
                }
            }
        }
        catch
        {
            // Update checks are best-effort; never surface an error to the user.
        }
    }

    private static string CurrentAssemblyVersion() =>
        typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private static (string Owner, string Repo) ParseRepo(string repoUrl)
    {
        var parts = repoUrl.TrimEnd('/').Split('/');
        return parts.Length >= 2 ? (parts[^2], parts[^1]) : ("", "");
    }
}
