// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

namespace AssetStore.App.Services;

/// <summary>Builds download/clone links and commands for an asset repository.</summary>
public static class GitLinks
{
    /// <summary>URL of a source archive (.zip) at a specific commit (GitHub/GitLab style).</summary>
    public static string ArchiveZip(string repo, string commit) =>
        $"{repo.TrimEnd('/')}/archive/{commit}.zip";

    /// <summary>Raw URL of a file at the repository root at the pinned commit. Uses
    /// raw.githubusercontent.com (not github.com/raw) so fetch() requests receive CORS headers.</summary>
    public static string RawRepoFile(string repo, string commit, string path)
    {
        var raw = repo.TrimEnd('/').Replace("https://github.com/", "https://raw.githubusercontent.com/");
        return $"{raw}/{commit}/{path.TrimStart('/')}";
    }

    /// <summary>Raw URL of a file inside AssetData/ at the pinned commit.</summary>
    public static string RawAssetFile(string repo, string commit, string assetRelativePath) =>
        RawRepoFile(repo, commit, $"AssetData/{assetRelativePath.TrimStart('/')}");

    /// <summary>Web URL browsing the repository at the pinned commit.</summary>
    public static string TreeAtCommit(string repo, string commit) =>
        $"{repo.TrimEnd('/')}/tree/{commit}";

    /// <summary>Web URL listing the repository's forks (GitHub style).</summary>
    public static string Forks(string repo) =>
        $"{repo.TrimEnd('/')}/forks";

    /// <summary>Web URL to open a new issue on the asset repository.</summary>
    public static string NewIssue(string repo) =>
        $"{repo.TrimEnd('/')}/issues/new";

    /// <summary>Web URL listing the repository's releases (changelog).</summary>
    public static string Releases(string repo) =>
        $"{repo.TrimEnd('/')}/releases";

    /// <summary>Web URL of the latest release.</summary>
    public static string ReleasesLatest(string repo) =>
        $"{repo.TrimEnd('/')}/releases/latest";

    /// <summary>
    /// Stable download URL for a named asset of the <em>latest</em> release — GitHub redirects
    /// <c>/releases/latest/download/&lt;name&gt;</c> to whichever release is current, so links never go stale.
    /// </summary>
    public static string LatestAssetDownload(string repo, string assetName) =>
        $"{repo.TrimEnd('/')}/releases/latest/download/{assetName}";

    /// <summary>GitHub API URL listing the full file tree at a commit (for the repository viewer), or null.</summary>
    public static string? TreeApi(string repo, string commit)
    {
        var trimmed = repo.TrimEnd('/');
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }

        var parts = trimmed.Split('/');
        if (parts.Length < 2)
        {
            return null;
        }

        return $"https://api.github.com/repos/{parts[^2]}/{parts[^1]}/git/trees/{commit}?recursive=1";
    }

    /// <summary>Prefilled "report this asset" issue on the registry repository.</summary>
    public static string ReportAsset(string registryOwner, string registryRepo, string assetId, string assetRepo)
    {
        var title = Uri.EscapeDataString($"Report: {assetId}");
        var body = Uri.EscapeDataString($"Asset: {assetId}\nRepo: {assetRepo}\n\nReason:\n");
        return $"https://github.com/{registryOwner}/{registryRepo}/issues/new?title={title}&body={body}";
    }

    /// <summary>Command to add the asset as a NuGet package reference.</summary>
    public static string AddPackageCommand(string packageId, string? version) =>
        version is null ? $"dotnet add package {packageId}" : $"dotnet add package {packageId} --version {version}";

    /// <summary>Web URL listing a repository's pull requests.</summary>
    public static string PullRequests(string owner, string repo) =>
        $"https://github.com/{owner}/{repo}/pulls";

    /// <summary>
    /// GitHub "create a new file" URL with the path and contents prefilled. If the user lacks
    /// write access, GitHub offers to fork the repo and open a pull request from the fork —
    /// which is exactly the manual submission flow, assisted.
    /// </summary>
    public static string NewRegistryFile(string owner, string repo, string branch, string assetId, string contentJson)
    {
        var filename = $"registry/{assetId}.json";
        var value = Uri.EscapeDataString(contentJson);
        return $"https://github.com/{owner}/{repo}/new/{branch}?filename={filename}&value={value}";
    }

    /// <summary>GitHub "edit this file" URL for an existing registry entry (offers to fork on save).</summary>
    public static string EditRegistryFile(string owner, string repo, string branch, string assetId) =>
        $"https://github.com/{owner}/{repo}/edit/{branch}/registry/{assetId}.json";

    /// <summary>Web URL viewing an existing registry entry (from where it can be deleted via the UI).</summary>
    public static string RegistryFileBlob(string owner, string repo, string branch, string assetId) =>
        $"https://github.com/{owner}/{repo}/blob/{branch}/registry/{assetId}.json";
}
