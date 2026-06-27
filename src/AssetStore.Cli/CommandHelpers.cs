// Copyright (c) Stride contributors (https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using AssetStore.Cli.Commands;
using AssetStore.Core.Git;
using AssetStore.Core.Indexing;
using AssetStore.Core.Validation;

namespace AssetStore.Cli;

/// <summary>Shared helpers for resolving paths and wiring the index builder.</summary>
internal static class CommandHelpers
{
    /// <summary>
    /// Resolves the AssetContainer root: the given path, the current directory if it looks like a
    /// container, or a child <c>AssetContainer</c> folder.
    /// </summary>
    public static string ResolveContainer(string? container)
    {
        if (!string.IsNullOrWhiteSpace(container))
        {
            return Path.GetFullPath(container);
        }

        var cwd = Environment.CurrentDirectory;
        if (LooksLikeContainer(cwd))
        {
            return cwd;
        }

        var child = Path.Combine(cwd, "AssetContainer");
        if (LooksLikeContainer(child))
        {
            return child;
        }

        throw new DirectoryNotFoundException(
            "Could not locate an AssetContainer (a folder with schemas/ and registry/). Pass --container.");
    }

    /// <summary>Defaults the workspace to the container's parent directory (sibling checkouts).</summary>
    public static string ResolveWorkspace(string? workspace, string container) =>
        string.IsNullOrWhiteSpace(workspace)
            ? Directory.GetParent(container)!.FullName
            : Path.GetFullPath(workspace);

    public static IndexBuilder CreateBuilder(string container, SharedSettings settings)
    {
        var validator = AssetValidator.FromContainer(container);
        IAssetSource source = settings.Source.ToLowerInvariant() switch
        {
            "git" => new GitAssetSource(settings.Cache ?? Path.Combine(Path.GetTempPath(), "assetstore-cache")),
            "local" => new LocalAssetSource(ResolveWorkspace(settings.Workspace, container)),
            _ => throw new ArgumentException($"Unknown source '{settings.Source}'. Use 'local' or 'git'."),
        };

        Func<string, int?>? stars = settings.Stars
            ? new GitHubStars(Environment.GetEnvironmentVariable("GITHUB_TOKEN")).Get
            : null;

        var git = new GitClient();
        return new IndexBuilder(container, source, validator, stars, git.ListRemoteTags, git.ResolveRemoteCommit);
    }

    private static bool LooksLikeContainer(string path) =>
        Directory.Exists(Path.Combine(path, "schemas")) && Directory.Exists(Path.Combine(path, "registry"));
}
