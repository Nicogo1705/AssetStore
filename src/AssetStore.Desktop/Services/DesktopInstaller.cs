// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using AssetStore.Core.Git;
using AssetStore.Core.Hashing;
using AssetStore.Core.Models;
using AssetStore.Core.Projects;

namespace AssetStore.Desktop.Services;

/// <summary>A filesystem entry shown by the project picker.</summary>
public enum FsKind { Directory, Solution, Project }

public sealed record FsEntry(string Name, string Path, FsKind Kind);

/// <summary>Result of an install attempt.</summary>
public sealed record InstallResult(bool Success, IReadOnlyList<string> Messages);

/// <summary>An asset found installed under a project's clone folder.</summary>
public sealed record InstalledAsset(
    string Id,
    string Name,
    string Path,
    string InstalledVersion,
    string InstalledCommit,
    string? LatestCommit,
    string Status); // up-to-date | outdated | unknown

/// <summary>
/// Server-side install: browse the local filesystem, read a solution's projects, and install an
/// asset (and its dependencies) by cloning it next to the project and adding a ProjectReference.
/// Desktop-only (requires local filesystem + git).
/// </summary>
public sealed class DesktopInstaller(GitClient? git = null)
{
    private readonly GitClient _git = git ?? new GitClient();

    /// <summary>Lists directories and .sln/.slnx/.csproj files at a path (drives when path is null).</summary>
    public IReadOnlyList<FsEntry> Browse(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .Select(d => new FsEntry(d.Name, d.RootDirectory.FullName, FsKind.Directory))
                .ToList();
        }

        var full = Path.GetFullPath(path);
        var entries = new List<FsEntry>();

        var parent = Directory.GetParent(full);
        if (parent is not null)
        {
            entries.Add(new FsEntry("..", parent.FullName, FsKind.Directory));
        }

        try
        {
            foreach (var dir in Directory.GetDirectories(full).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                entries.Add(new FsEntry(Path.GetFileName(dir), dir, FsKind.Directory));
            }

            foreach (var file in Directory.GetFiles(full).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is ".sln" or ".slnx")
                {
                    entries.Add(new FsEntry(Path.GetFileName(file), file, FsKind.Solution));
                }
                else if (ext == ".csproj")
                {
                    entries.Add(new FsEntry(Path.GetFileName(file), file, FsKind.Project));
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Folder not readable (permissions, special system folders) — show just the parent entry.
        }

        return entries;
    }

    /// <summary>Returns the candidate target projects for a picked .sln/.slnx/.csproj.</summary>
    public IReadOnlyList<SolutionProject> ReadTargets(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext == ".csproj"
            ? [new SolutionProject(Path.GetFileNameWithoutExtension(path), Path.GetFullPath(path))]
            : SolutionInspector.ReadProjects(path);
    }

    /// <summary>
    /// Installs <paramref name="asset"/> at <paramref name="reference"/> into each target project:
    /// clones the asset (and resolved dependencies) under a sibling <c>StoreAssets/</c> folder and
    /// adds a ProjectReference to the asset's project.
    /// </summary>
    public InstallResult Install(
        IndexedAsset asset,
        string reference,
        IReadOnlyList<string> targetCsprojPaths,
        IReadOnlyDictionary<string, IndexedAsset> catalog,
        string cloneDir)
    {
        var messages = new List<string>();

        if (!_git.IsAvailable())
        {
            return new InstallResult(false, ["git was not found on PATH."]);
        }

        if (targetCsprojPaths.Count == 0)
        {
            return new InstallResult(false, ["Select at least one target project."]);
        }

        if (string.IsNullOrWhiteSpace(cloneDir))
        {
            return new InstallResult(false, ["Choose a folder to clone assets into."]);
        }

        try
        {
            var storeRoot = Path.GetFullPath(cloneDir);
            Directory.CreateDirectory(storeRoot);

            // Clone the asset plus its resolved dependencies (so inter-asset references resolve).
            var assetFolder = Clone(asset.Repo, reference, storeRoot, messages);
            var missingDeps = false;
            foreach (var depId in asset.Latest.ResolvedDependencies)
            {
                if (catalog.TryGetValue(depId, out var dep))
                {
                    Clone(dep.Repo, dep.Latest.Ref, storeRoot, messages);
                }
                else
                {
                    missingDeps = true;
                    messages.Add($"⚠ Dependency '{depId}' is not in the catalog — the project won't compile until it's available.");
                }
            }

            // Integrity: for the 'latest' ref the index knows the expected content hash.
            var assetData = Path.Combine(storeRoot, assetFolder, "AssetData");
            if (string.Equals(reference, asset.Latest.Ref, StringComparison.Ordinal)
                && !string.IsNullOrEmpty(asset.Latest.ContentHash)
                && Directory.Exists(assetData))
            {
                var actual = ContentHasher.HashDirectory(assetData).Hash;
                messages.Add(string.Equals(actual, asset.Latest.ContentHash, StringComparison.OrdinalIgnoreCase)
                    ? "✓ Content hash verified."
                    : $"⚠ Content hash mismatch — the source may have changed since it was indexed.");
            }

            var assetCsproj = CsprojInspector.FindProjects(assetData).FirstOrDefault();
            if (assetCsproj is null)
            {
                return new InstallResult(false, [.. messages, "No .csproj found in the asset's AssetData folder."]);
            }

            foreach (var target in targetCsprojPaths)
            {
                var added = CsprojEditor.AddProjectReference(target, assetCsproj);
                messages.Add(added
                    ? $"✓ Added reference to {Path.GetFileName(target)}"
                    : $"• {Path.GetFileName(target)} already references the asset");
            }

            return new InstallResult(!missingDeps, messages);
        }
        catch (Exception ex)
        {
            messages.Add($"✗ {ex.Message}");
            return new InstallResult(false, messages);
        }
    }

    /// <summary>
    /// Scans <paramref name="folder"/> for installed assets (subfolders with AssetData/manifest.json)
    /// and reports whether each is up to date versus the catalog.
    /// </summary>
    public IReadOnlyList<InstalledAsset> ScanInstalled(string folder, IReadOnlyDictionary<string, IndexedAsset> catalog)
    {
        var result = new List<InstalledAsset>();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return result;
        }

        foreach (var dir in Directory.GetDirectories(folder).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var manifestPath = Path.Combine(dir, "AssetData", "manifest.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            AssetManifest manifest;
            try
            {
                manifest = AssetStore.Core.Serialization.AssetStoreJson.Deserialize<AssetManifest>(File.ReadAllText(manifestPath));
            }
            catch
            {
                continue;
            }

            var installedCommit = _git.ResolveCommit(dir, "HEAD") ?? "";
            catalog.TryGetValue(manifest.Id, out var entry);
            var latestCommit = entry?.Latest.Commit;

            var status = latestCommit is null ? "unknown"
                : string.Equals(latestCommit, installedCommit, StringComparison.OrdinalIgnoreCase) ? "up-to-date"
                : "outdated";

            result.Add(new InstalledAsset(
                manifest.Id, manifest.Name, dir, manifest.Version,
                installedCommit, latestCommit, status));
        }

        return result;
    }

    /// <summary>Updates an installed asset to the tip of a ref (returns the new commit or null).</summary>
    public string? UpdateInstalled(string assetDir, string reference)
    {
        _git.UpdateToRef(assetDir, reference);
        return _git.ResolveCommit(assetDir, "HEAD");
    }

    private string Clone(string repo, string reference, string storeRoot, List<string> messages)
    {
        var folder = GitClient.SafeRepoFolderName(repo);
        var dest = Path.Combine(storeRoot, folder);
        if (Directory.Exists(Path.Combine(dest, ".git")))
        {
            _git.UpdateToRef(dest, reference);
            messages.Add($"• Updated {folder} ({reference})");
        }
        else
        {
            if (Directory.Exists(dest))
            {
                Directory.Delete(dest, recursive: true);
            }

            _git.ShallowClone(repo, reference, dest);
            messages.Add($"✓ Cloned {folder} ({reference})");
        }

        return folder;
    }
}
