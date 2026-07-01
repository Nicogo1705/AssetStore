// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

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

    public string CurrentVersion { get; } = CurrentAssemblyVersion();

    public string? LatestVersion { get; private set; }

    /// <summary>Web URL of the latest release (release notes), when known.</summary>
    public string? ReleaseUrl { get; private set; }

    /// <summary>Direct download URL of the build for the current OS/architecture, when known.</summary>
    public string? DownloadUrl { get; private set; }

    /// <summary>Queries the latest release once. Safe to call repeatedly (no-ops after the first).</summary>
    public async Task CheckAsync(CancellationToken ct = default)
    {
        if (Checked)
        {
            return;
        }

        Checked = true;

        try
        {
            var (owner, repo) = ParseRepo(app.Repo);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"repos/{owner}/{repo}/releases/latest");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
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
                UpdateAvailable = true;
                var build = DesktopBuilds.Current();
                DownloadUrl = build is not null
                    ? GitLinks.LatestAssetDownload(app.Repo, build.AssetName)
                    : GitLinks.ReleasesLatest(app.Repo);
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
