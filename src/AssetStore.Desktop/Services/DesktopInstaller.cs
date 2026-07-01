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
    string InstalledCommit,
    string? LatestCommit,
    string Status); // up-to-date | outdated | unknown | broken

/// <summary>A store asset referenced by a specific project (via ProjectReference or PackageReference).</summary>
public sealed record ProjectAsset(
    string Id,
    string Name,
    string Status,           // up-to-date | outdated | unknown | broken
    string InstalledCommit,
    string? LatestCommit,
    string Kind,             // "local" | "nuget"
    string CloneRoot,        // local: the clone folder on disk (else "")
    string ReferencedCsproj, // local: absolute path of the referenced .csproj
    string? PackageId);      // nuget: the package id

/// <summary>A project within a solution and the store assets it references.</summary>
public sealed record ProjectNode(string Name, string CsprojPath, IReadOnlyList<ProjectAsset> Assets);

/// <summary>A solution (or lone project) and its projects' store assets.</summary>
public sealed record SolutionView(string Path, string Name, IReadOnlyList<ProjectNode> Projects);

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
    /// NuGet install: add a <c>&lt;PackageReference&gt;</c> for the asset's published package to each
    /// target project. No source is cloned. Requires the asset to declare a NuGet package.
    /// </summary>
    public InstallResult InstallNuget(IndexedAsset asset, IReadOnlyList<string> targetCsprojPaths)
    {
        var nuget = asset.Manifest.Nuget;
        if (nuget is null)
        {
            return new InstallResult(false, ["This asset is not published on NuGet."]);
        }

        if (targetCsprojPaths.Count == 0)
        {
            return new InstallResult(false, ["Select at least one target project."]);
        }

        var messages = new List<string>();
        try
        {
            foreach (var target in targetCsprojPaths)
            {
                var added = CsprojEditor.AddPackageReference(target, nuget.PackageId, nuget.PackageVersion);
                messages.Add(added
                    ? $"✓ Added package {nuget.PackageId} to {Path.GetFileName(target)}"
                    : $"• {Path.GetFileName(target)} already references {nuget.PackageId}");
            }

            return new InstallResult(true, messages);
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
            var assetDataDir = Path.Combine(dir, "AssetData");
            var manifestPath = Path.Combine(assetDataDir, "manifest.json");

            // A folder that was meant to hold an asset (it has an AssetData/ dir or a .git clone) but has
            // no readable manifest is an incomplete/broken install — a failed or partial clone, source that
            // was deleted, or nothing left but build output (obj/). Surface it as "broken" so the user isn't
            // left wondering why it's missing, rather than silently skipping it.
            var looksLikeAsset = Directory.Exists(assetDataDir) || Directory.Exists(Path.Combine(dir, ".git"));

            if (!File.Exists(manifestPath))
            {
                if (looksLikeAsset)
                {
                    result.Add(new InstalledAsset(
                        Id: "", Name: Path.GetFileName(dir), Path: dir,
                        InstalledCommit: "", LatestCommit: null, Status: "broken"));
                }

                continue;
            }

            AssetManifest manifest;
            try
            {
                manifest = AssetStore.Core.Serialization.AssetStoreJson.Deserialize<AssetManifest>(File.ReadAllText(manifestPath));
            }
            catch
            {
                result.Add(new InstalledAsset(
                    Id: "", Name: Path.GetFileName(dir), Path: dir,
                    InstalledCommit: "", LatestCommit: null, Status: "broken"));
                continue;
            }

            var installedCommit = _git.ResolveCommit(dir, "HEAD") ?? "";
            catalog.TryGetValue(manifest.Id, out var entry);
            var latestCommit = entry?.Latest.Commit;

            var status = latestCommit is null ? "unknown"
                : string.Equals(latestCommit, installedCommit, StringComparison.OrdinalIgnoreCase) ? "up-to-date"
                : "outdated";

            result.Add(new InstalledAsset(
                manifest.Id, manifest.Name, dir,
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

    /// <summary>
    /// Analyses a solution (or lone .csproj): lists its projects and, for each, the store assets it
    /// references — local (a ProjectReference into a cloned <c>AssetData/</c>) or NuGet (a PackageReference
    /// matching a catalog asset's published package) — with an up-to-date/outdated/broken status.
    /// </summary>
    public SolutionView Analyze(string solutionOrCsproj, IReadOnlyDictionary<string, IndexedAsset> catalog)
    {
        var full = Path.GetFullPath(solutionOrCsproj);
        var nodes = new List<ProjectNode>();

        IReadOnlyList<SolutionProject> projects;
        try
        {
            projects = ReadTargets(full);
        }
        catch
        {
            projects = [];
        }

        foreach (var project in projects.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            nodes.Add(new ProjectNode(project.Name, project.Path, AnalyzeProject(project.Path, catalog)));
        }

        return new SolutionView(full, Path.GetFileName(full), nodes);
    }

    private IReadOnlyList<ProjectAsset> AnalyzeProject(
        string csprojPath, IReadOnlyDictionary<string, IndexedAsset> catalog)
    {
        var assets = new List<ProjectAsset>();
        if (!File.Exists(csprojPath))
        {
            return assets;
        }

        // Local installs: ProjectReferences that point into a cloned store asset.
        foreach (var include in SafeProjectReferences(csprojPath))
        {
            var referenced = ResolveInclude(csprojPath, include);
            var clone = FindStoreClone(referenced);
            if (clone is null)
            {
                continue; // an ordinary ProjectReference, not a store asset
            }

            var (cloneRoot, hasManifest) = clone.Value;
            if (!hasManifest)
            {
                assets.Add(new ProjectAsset(
                    "", Path.GetFileName(cloneRoot), "broken", "", null, "local", cloneRoot, referenced, null));
                continue;
            }

            var manifest = TryReadManifest(Path.Combine(cloneRoot, "AssetData", "manifest.json"));
            if (manifest is null)
            {
                assets.Add(new ProjectAsset(
                    "", Path.GetFileName(cloneRoot), "broken", "", null, "local", cloneRoot, referenced, null));
                continue;
            }

            var installed = _git.ResolveCommit(cloneRoot, "HEAD") ?? "";
            catalog.TryGetValue(manifest.Id, out var entry);
            var latest = entry?.Latest.Commit;
            assets.Add(new ProjectAsset(
                manifest.Id, manifest.Name, StatusOf(installed, latest),
                installed, latest, "local", cloneRoot, referenced, null));
        }

        // NuGet installs: PackageReferences matching a catalog asset's published package.
        foreach (var (name, version) in SafePackageReferences(csprojPath))
        {
            var match = catalog.Values.FirstOrDefault(a =>
                a.Manifest.Nuget is { } n && string.Equals(n.PackageId, name, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                assets.Add(new ProjectAsset(
                    match.Id, match.Manifest.Name, "unknown", version ?? "", null, "nuget", "", csprojPath, name));
            }
        }

        return assets;
    }

    /// <summary>Removes a local asset's ProjectReference from a project. Returns true if modified.</summary>
    public bool UninstallLocal(string csprojPath, string referencedCsproj) =>
        CsprojEditor.RemoveProjectReference(csprojPath, referencedCsproj);

    /// <summary>Removes a NuGet asset's PackageReference from a project. Returns true if modified.</summary>
    public bool UninstallNuget(string csprojPath, string packageId) =>
        CsprojEditor.RemovePackageReference(csprojPath, packageId);

    /// <summary>Deletes a cloned asset folder from disk (used when no project references it any more).</summary>
    public bool DeleteClone(string cloneRoot)
    {
        if (string.IsNullOrWhiteSpace(cloneRoot) || !Directory.Exists(cloneRoot))
        {
            return false;
        }

        Directory.Delete(cloneRoot, recursive: true);
        return true;
    }

    private static string StatusOf(string installedCommit, string? latestCommit) =>
        latestCommit is null ? "unknown"
        : string.Equals(latestCommit, installedCommit, StringComparison.OrdinalIgnoreCase) ? "up-to-date"
        : "outdated";

    private static string ResolveInclude(string csprojPath, string include)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(csprojPath))!;
        return Path.GetFullPath(Path.Combine(dir, include.Replace('\\', Path.DirectorySeparatorChar)));
    }

    // Walks up from a referenced .csproj to the store clone root: the first ancestor with an AssetData/
    // folder. HasManifest distinguishes a healthy asset from a broken/partial clone.
    private static (string Root, bool HasManifest)? FindStoreClone(string referencedCsprojAbs)
    {
        var dir = Path.GetDirectoryName(referencedCsprojAbs);
        while (dir is not null)
        {
            var assetData = Path.Combine(dir, "AssetData");
            if (File.Exists(Path.Combine(assetData, "manifest.json")))
            {
                return (dir, true);
            }

            if (Directory.Exists(assetData))
            {
                return (dir, false);
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        return null;
    }

    private static AssetManifest? TryReadManifest(string manifestPath)
    {
        try
        {
            return AssetStore.Core.Serialization.AssetStoreJson.Deserialize<AssetManifest>(File.ReadAllText(manifestPath));
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<string> SafeProjectReferences(string csprojPath)
    {
        try
        {
            return CsprojInspector.GetProjectReferences(csprojPath);
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyList<(string Name, string? Version)> SafePackageReferences(string csprojPath)
    {
        try
        {
            return CsprojInspector.GetPackageReferences(csprojPath);
        }
        catch
        {
            return [];
        }
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
