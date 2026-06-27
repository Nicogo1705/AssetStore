// Copyright (c) Stride contributors (https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

namespace AssetStore.App.Services;

/// <summary>Builds download/clone links and commands for an asset repository.</summary>
public static class GitLinks
{
    /// <summary>URL of a source archive (.zip) at a specific commit (GitHub/GitLab style).</summary>
    public static string ArchiveZip(string repo, string commit) =>
        $"{repo.TrimEnd('/')}/archive/{commit}.zip";

    /// <summary>Web URL browsing the repository at the pinned commit.</summary>
    public static string TreeAtCommit(string repo, string commit) =>
        $"{repo.TrimEnd('/')}/tree/{commit}";

    /// <summary>Command to clone the repository.</summary>
    public static string CloneCommand(string repo) => $"git clone {repo}";

    /// <summary>Command to add the asset as a NuGet package reference.</summary>
    public static string AddPackageCommand(string packageId, string? version) =>
        version is null ? $"dotnet add package {packageId}" : $"dotnet add package {packageId} --version {version}";
}
