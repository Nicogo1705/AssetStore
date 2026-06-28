// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using AssetStore.Core.Git;
using AssetStore.Core.Models;

namespace AssetStore.Core.Indexing;

/// <summary>
/// Resolves assets from local working copies sitting next to each other in a workspace directory.
/// The folder name is derived from the repository URL's last segment.
/// </summary>
/// <remarks>Used by the prototype and tests so the pipeline runs without network access.</remarks>
public sealed class LocalAssetSource(string workspaceDirectory, GitClient? git = null) : IAssetSource
{
    private readonly GitClient _git = git ?? new GitClient();

    public AssetCheckout Fetch(RegistryEntry entry)
    {
        // Same folder-naming + path-traversal guard the git source uses (also strips a .git suffix).
        var folderName = GitClient.SafeRepoFolderName(entry.Repo);
        var root = Path.Combine(workspaceDirectory, folderName);

        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(
                $"Local checkout not found for '{entry.Id}': expected '{root}'. " +
                "Clone the asset repository next to AssetContainer, or use a git-backed source.");
        }

        var assetData = Path.Combine(root, "AssetData");
        var commit = _git.IsAvailable() ? _git.ResolveCommit(root, entry.Latest.Ref) ?? _git.ResolveCommit(root) : null;
        return new AssetCheckout(root, assetData, commit);
    }
}
