// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

namespace AssetStore.App.Services;

/// <summary>Builds download/clone links and commands for an asset repository.</summary>
public static class GitLinks
{
    /// <summary>URL of a source archive (.zip) at a specific commit (GitHub/GitLab style).</summary>
    public static string ArchiveZip(string repo, string commit) =>
        $"{repo.TrimEnd('/')}/archive/{commit}.zip";

    /// <summary>Raw URL of a file inside AssetData/ at the pinned commit (GitHub style).</summary>
    public static string RawAssetFile(string repo, string commit, string assetRelativePath) =>
        $"{repo.TrimEnd('/')}/raw/{commit}/AssetData/{assetRelativePath.TrimStart('/')}";

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
}
