// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Diagnostics;
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

/// <summary>A store asset referenced by a specific project (via ProjectReference or PackageReference).</summary>
public sealed record ProjectAsset(
    string Id,
    string Name,
    string Status,           // up-to-date | outdated | unknown | broken | missing
    string InstalledCommit,
    string? LatestCommit,
    string Kind,             // "local" | "nuget"
    string CloneRoot,        // local: the clone folder on disk (else "")
    string ReferencedCsproj, // local: absolute path of the referenced .csproj
    string? PackageId,       // nuget: the package id
    string RawInclude = "",  // local: the verbatim csproj Include (needed to remove global-cache refs)
    string Ref = "",         // local: the ref the clone follows, read from its cache path ("" = legacy)
    string? StrideVersion = null); // Stride version the asset targets (from its csproj / the index)

/// <summary>An asset clone sitting in the shared cache (the My assets page).</summary>
public sealed record CachedAsset(
    string Id,               // "" when the manifest is unreadable
    string Name,
    string Ref,              // followed ref from the path ("" = legacy flat layout)
    string CloneRoot,
    string InstalledCommit,
    string Status,           // up-to-date | outdated | unknown | broken
    long SizeBytes);

/// <summary>A project within a solution and the store assets it references.</summary>
public sealed record ProjectNode(string Name, string CsprojPath, IReadOnlyList<ProjectAsset> Assets,
    string? StrideVersion = null); // Stride version the project targets (null when none detected)

/// <summary>A store-asset project still listed in a solution whose files are gone (a "Remove" that
/// never cleaned the .sln). Kept visible so the user can download the asset again or drop the entry.</summary>
public sealed record StoreSolutionProject(string Name, string CsprojPath, string? AssetId);

/// <summary>A solution (or lone project) and its projects' store assets, plus any stale store entries.</summary>
public sealed record SolutionView(
    string Path, string Name,
    IReadOnlyList<ProjectNode> Projects,
    IReadOnlyList<StoreSolutionProject> Dangling);

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

    /// <summary>
    /// Returns the candidate target projects for a picked .sln/.slnx/.csproj — excluding store-asset
    /// projects, which live in the solution only so Visual Studio can load the ProjectReferences (you
    /// don't install an asset into another asset's project).
    /// </summary>
    public IReadOnlyList<SolutionProject> ReadTargets(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var projects = ext == ".csproj"
            ? new List<SolutionProject> { new(Path.GetFileNameWithoutExtension(path), Path.GetFullPath(path)) }
            : SolutionInspector.ReadProjects(path).ToList();

        return projects.Where(p => !IsStoreAssetProject(Path.GetFullPath(p.Path))).ToList();
    }

    /// <summary>
    /// True when a .csproj is an installed store asset (not a user project): either it sits under the
    /// global asset cache (matches even after its files were deleted, leaving a dangling .sln entry),
    /// or an ancestor has an <c>AssetData/manifest.json</c> (covers "into a folder" installs).
    /// </summary>
    private static bool IsStoreAssetProject(string csprojAbs) =>
        csprojAbs.StartsWith(GlobalCacheRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || FindStoreClone(csprojAbs) is not null;

    /// <summary>
    /// Lists store-asset projects that a solution still references but whose <c>.csproj</c> is gone from
    /// disk — the stale entries left when an asset's clone was deleted (a "Remove" that never cleaned the
    /// .sln). They aren't removed automatically: a missing file may just be a shared-source dependency the
    /// user hasn't downloaded yet, so we surface them for the user to download again or drop. No-op for a
    /// lone .csproj target.
    /// </summary>
    public IReadOnlyList<StoreSolutionProject> ListDanglingStoreProjects(
        string solutionOrCsproj, IReadOnlyDictionary<string, IndexedAsset> catalog)
    {
        var ext = Path.GetExtension(solutionOrCsproj).ToLowerInvariant();
        if (ext is not (".sln" or ".slnx"))
        {
            return [];
        }

        List<SolutionProject> projects;
        try
        {
            projects = SolutionInspector.ReadProjects(solutionOrCsproj).ToList();
        }
        catch
        {
            return [];
        }

        var result = new List<StoreSolutionProject>();
        foreach (var p in projects)
        {
            var abs = Path.GetFullPath(p.Path);
            if (!IsStoreAssetProject(abs) || File.Exists(abs))
            {
                continue;
            }

            // Try to map the (global-cache) clone folder back to a catalog asset so the UI can offer
            // to re-download it rather than only remove the stale entry.
            string? assetId = null;
            if (abs.StartsWith(GlobalCacheRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                var (_, folder) = GlobalCachePartsOf(abs);
                assetId = catalog.Values.FirstOrDefault(a =>
                    string.Equals(GitClient.SafeRepoFolderName(a.Repo), folder, StringComparison.OrdinalIgnoreCase))?.Id;
            }

            result.Add(new StoreSolutionProject(p.Name, abs, assetId));
        }

        return result;
    }

    /// <summary>Removes a single project entry from a .sln/.slnx by path (works even if the file is gone).
    /// Returns true on success.</summary>
    public bool RemoveFromSolution(string solutionOrCsproj, string csprojPath)
    {
        var ext = Path.GetExtension(solutionOrCsproj).ToLowerInvariant();
        if (ext is not (".sln" or ".slnx"))
        {
            return false;
        }

        var (exitCode, _, _) = RunDotnet(["sln", solutionOrCsproj, "remove", csprojPath],
            Path.GetDirectoryName(Path.GetFullPath(solutionOrCsproj)));
        return exitCode == 0;
    }

    /// <summary>
    /// The per-machine global asset cache. Assets installed in "global" mode are cloned here, and the
    /// project reference is written with an MSBuild property-function path that resolves to this folder on
    /// <em>any</em> machine — so the source can be shared and teammates just download the assets.
    /// Layout: <c>Assets\&lt;ref&gt;\&lt;repo-folder&gt;</c> — the followed ref (branch or tag) is part of
    /// the path, so different versions coexist and "up-to-date" is checked against THAT ref, not blindly
    /// against the index's latest. (Clones from before this layout sit directly under Assets\ and are
    /// still recognized.)
    /// </summary>
    public static string GlobalCacheRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StrideAssetStore", "Assets");

    /// <summary>A ref name as a filesystem-safe single folder name (e.g. "feature/x" → "feature-x").</summary>
    public static string SafeRefFolderName(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return "unknown";
        }

        var safe = new string(reference.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-').ToArray());
        return safe.Trim('-', '.') is { Length: > 0 } trimmed ? trimmed : "unknown";
    }

    // MSBuild resolves this to GlobalCacheRoot at evaluation time, on every machine/OS.
    private const string GlobalCacheInclude =
        @"$([System.Environment]::GetFolderPath(SpecialFolder.ApplicationData))\StrideAssetStore\Assets";

    private const string GlobalCacheMarker = "$([System.Environment]::GetFolderPath(SpecialFolder.ApplicationData))";

    /// <summary>
    /// Installs <paramref name="asset"/> at <paramref name="reference"/> into each target project by cloning
    /// the asset (and resolved dependencies) and adding a ProjectReference to it. When
    /// <paramref name="globalCache"/> is true the clone lives in the shared per-machine cache and the reference
    /// is a portable MSBuild path; otherwise it clones into <paramref name="cloneDir"/> with a relative reference.
    /// </summary>
    public InstallResult Install(
        IndexedAsset asset,
        string reference,
        IReadOnlyList<string> targetCsprojPaths,
        IReadOnlyDictionary<string, IndexedAsset> catalog,
        string cloneDir,
        bool globalCache = false,
        string? solutionPath = null)
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

        if (!globalCache && string.IsNullOrWhiteSpace(cloneDir))
        {
            return new InstallResult(false, ["Choose a folder to clone assets into."]);
        }

        try
        {
            var storeRoot = globalCache ? GlobalCacheRoot : Path.GetFullPath(cloneDir);

            // Versioned layout: <root>/<ref>/<repo-folder> — the followed ref is part of the path.
            var refRoot = Path.Combine(storeRoot, SafeRefFolderName(reference));
            Directory.CreateDirectory(refRoot);

            // Clone the asset plus its resolved dependencies (so inter-asset references resolve), verifying
            // each against the content hash the index recorded (integrity for the whole set, not just the root).
            var assetFolder = Clone(asset.Repo, reference, refRoot, messages);
            VerifyHash(refRoot, assetFolder, string.Equals(reference, asset.Latest.Ref, StringComparison.Ordinal) ? asset.Latest.ContentHash : null, asset.Manifest.Name, messages);

            var missingDeps = false;
            var clonedCsprojs = new List<string>(); // asset + dep .csprojs, to register in the solution
            foreach (var depId in asset.Latest.ResolvedDependencies)
            {
                if (catalog.TryGetValue(depId, out var dep))
                {
                    // Deps live in the SAME ref folder as the dependent asset: inter-asset
                    // ProjectReferences are relative ("../<depFolder>/AssetData/…"), so the dep
                    // must sit next to the asset for them to resolve.
                    var depFolder = Clone(dep.Repo, dep.Latest.Ref, refRoot, messages);
                    VerifyHash(refRoot, depFolder, dep.Latest.ContentHash, dep.Manifest.Name, messages);
                    var depCsproj = CsprojInspector.FindProjects(Path.Combine(refRoot, depFolder, "AssetData")).FirstOrDefault();
                    if (depCsproj is not null)
                    {
                        clonedCsprojs.Add(depCsproj);
                    }
                }
                else
                {
                    missingDeps = true;
                    messages.Add($"⚠ Dependency '{depId}' is not in the catalog — the project won't compile until it's available.");
                }
            }

            var assetData = Path.Combine(refRoot, assetFolder, "AssetData");
            var assetCsproj = CsprojInspector.FindProjects(assetData).FirstOrDefault();
            if (assetCsproj is null)
            {
                return new InstallResult(false, [.. messages, "No .csproj found in the asset's AssetData folder."]);
            }

            clonedCsprojs.Insert(0, assetCsproj);

            // In global mode the reference is portable (resolves via MSBuild on any machine); otherwise relative.
            var globalInclude = globalCache
                ? $"{GlobalCacheInclude}\\{Path.GetRelativePath(storeRoot, assetCsproj).Replace('/', '\\')}"
                : null;

            // Each target is edited independently so one locked/malformed .csproj can't leave the batch half-done.
            var anyTargetError = false;
            foreach (var target in targetCsprojPaths)
            {
                try
                {
                    var added = globalInclude is not null
                        ? CsprojEditor.AddRawProjectReference(target, globalInclude)
                        : CsprojEditor.AddProjectReference(target, assetCsproj);
                    messages.Add(added
                        ? $"✓ Added reference to {Path.GetFileName(target)}"
                        : $"• {Path.GetFileName(target)} already references the asset");
                }
                catch (Exception ex)
                {
                    anyTargetError = true;
                    messages.Add($"✗ {Path.GetFileName(target)}: {ex.Message}");
                }
            }

            // Register the asset (and its deps) in the solution so Visual Studio can load the referenced
            // projects — a ProjectReference to a project that isn't in the .sln shows as "project not found".
            AddToSolution(solutionPath, clonedCsprojs, messages);

            if (globalCache)
            {
                messages.Add("✓ Reference is portable — commit your source and teammates just download the asset.");
            }

            return new InstallResult(!missingDeps && !anyTargetError, messages);
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

    /// <summary>Updates an installed asset to the tip of a ref. Returns the new commit, or null on failure.</summary>
    public string? UpdateInstalled(string assetDir, string reference) =>
        _git.UpdateToRef(assetDir, reference) ? _git.ResolveCommit(assetDir, "HEAD") : null;

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
            // (ReadTargets already excludes store-asset projects — they're in the .sln only so VS loads the refs.)
            nodes.Add(new ProjectNode(project.Name, project.Path, AnalyzeProject(project.Path, catalog),
                SafeDetectStrideVersion(project.Path)));
        }

        return new SolutionView(full, Path.GetFileName(full), nodes, ListDanglingStoreProjects(full, catalog));
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
                // A portable reference into the global cache whose asset isn't downloaded on this machine yet:
                // surface it as "missing" so the user can fetch it with one click (the shared-source workflow).
                if (referenced.StartsWith(GlobalCacheRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    var (refName, folder) = GlobalCachePartsOf(referenced);
                    var known = catalog.Values.FirstOrDefault(a =>
                        string.Equals(GitClient.SafeRepoFolderName(a.Repo), folder, StringComparison.OrdinalIgnoreCase));
                    assets.Add(new ProjectAsset(
                        known?.Id ?? "", known?.Manifest.Name ?? folder, "missing", "", known?.Latest.Commit,
                        "local", Path.Combine(GlobalCacheRoot, refName ?? "", folder), referenced, null, include,
                        refName ?? ""));
                }

                continue; // otherwise an ordinary ProjectReference, not a store asset
            }

            var (cloneRoot, hasManifest) = clone.Value;
            if (!hasManifest)
            {
                assets.Add(new ProjectAsset(
                    "", Path.GetFileName(cloneRoot), "broken", "", null, "local", cloneRoot, referenced, null, include));
                continue;
            }

            var manifest = TryReadManifest(Path.Combine(cloneRoot, "AssetData", "manifest.json"));
            if (manifest is null)
            {
                assets.Add(new ProjectAsset(
                    "", Path.GetFileName(cloneRoot), "broken", "", null, "local", cloneRoot, referenced, null, include));
                continue;
            }

            var installed = _git.ResolveCommit(cloneRoot, "HEAD") ?? "";
            catalog.TryGetValue(manifest.Id, out var entry);
            // Compare against the ref the clone's path says it follows: a v1.0.0 clone is judged
            // against the v1.0.0 tag commit, not against the moving latest.
            var followedRef = RefOfClone(cloneRoot);
            var expected = ExpectedCommitFor(entry, followedRef);
            assets.Add(new ProjectAsset(
                manifest.Id, manifest.Name, StatusOf(installed, expected),
                installed, expected, "local", cloneRoot, referenced, null, include, followedRef ?? "",
                // What the INSTALLED clone actually targets (its csproj), not the index's opinion.
                SafeDetectStrideVersion(referenced) ?? entry?.Latest.DetectedStrideVersion));
        }

        // NuGet installs: PackageReferences matching a catalog asset's published package.
        foreach (var (name, version) in SafePackageReferences(csprojPath))
        {
            var match = catalog.Values.FirstOrDefault(a =>
                a.Manifest.Nuget is { } n && string.Equals(n.PackageId, name, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                assets.Add(new ProjectAsset(
                    match.Id, match.Manifest.Name, "unknown", version ?? "", null, "nuget", "", csprojPath, name,
                    StrideVersion: match.Latest.DetectedStrideVersion));
            }
        }

        return assets;
    }

    /// <summary><see cref="CsprojInspector.DetectStrideVersion"/> that returns null instead of throwing
    /// (missing file, malformed xml).</summary>
    private static string? SafeDetectStrideVersion(string csprojPath)
    {
        try
        {
            return File.Exists(csprojPath) ? CsprojInspector.DetectStrideVersion(csprojPath) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Removes a local asset's ProjectReference, matched by its verbatim Include (works for both
    /// relative and global-cache references). Returns true if modified.</summary>
    public bool UninstallLocal(string csprojPath, string rawInclude) =>
        CsprojEditor.RemoveRawProjectReference(csprojPath, rawInclude);

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

        ForceDeleteDirectory(cloneRoot);
        return true;
    }

    /// <summary>
    /// Recursively deletes a directory, first clearing read-only attributes. Git marks files under
    /// <c>.git</c> (pack/object files) read-only, which makes a plain <see cref="Directory.Delete(string, bool)"/>
    /// throw <see cref="UnauthorizedAccessException"/> on Windows.
    /// </summary>
    private static void ForceDeleteDirectory(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                var attributes = File.GetAttributes(file);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
            }
            catch
            {
                // best-effort; Directory.Delete will surface any real problem
            }
        }

        Directory.Delete(path, recursive: true);
    }

    private static string StatusOf(string installedCommit, string? latestCommit) =>
        latestCommit is null ? "unknown"
        : string.Equals(latestCommit, installedCommit, StringComparison.OrdinalIgnoreCase) ? "up-to-date"
        : "outdated";

    private static string ResolveInclude(string csprojPath, string include)
    {
        // Expand the global-cache MSBuild property function to the real folder, mirroring what MSBuild does.
        var expanded = include.Contains(GlobalCacheMarker, StringComparison.OrdinalIgnoreCase)
            ? include.Replace(GlobalCacheMarker, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), StringComparison.OrdinalIgnoreCase)
            : include;
        expanded = expanded.Replace('\\', Path.DirectorySeparatorChar);

        if (Path.IsPathRooted(expanded))
        {
            return Path.GetFullPath(expanded);
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(csprojPath))!;
        return Path.GetFullPath(Path.Combine(dir, expanded));
    }

    /// <summary>
    /// Splits a path under the global cache into (ref, repo folder). Handles both layouts:
    /// versioned <c>Assets\&lt;ref&gt;\&lt;folder&gt;\AssetData\…</c> and legacy
    /// <c>Assets\&lt;folder&gt;\AssetData\…</c> (ref null).
    /// </summary>
    private static (string? Ref, string Folder) GlobalCachePartsOf(string resolvedPath)
    {
        var rel = Path.GetRelativePath(GlobalCacheRoot, resolvedPath);
        var segments = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var versioned = segments.Length >= 2
            && !segments[1].Equals("AssetData", StringComparison.OrdinalIgnoreCase);
        return versioned ? (segments[0], segments[1]) : (null, segments[0]);
    }

    /// <summary>The ref a clone follows, read from its cache path (null for legacy/unversioned clones).</summary>
    private static string? RefOfClone(string cloneRoot)
    {
        if (cloneRoot.StartsWith(GlobalCacheRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return GlobalCachePartsOf(Path.Combine(cloneRoot, "AssetData")).Ref;
        }

        // Local layout <chosen-dir>/<ref>/<folder> — take the parent folder as the candidate ref.
        return Path.GetFileName(Path.GetDirectoryName(cloneRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
    }

    /// <summary>
    /// The commit the installed clone SHOULD be at, given the ref its path says it follows:
    /// a certified/tagged ref pins its own commit; the tracked branch compares to the index's
    /// latest; anything unknown falls back to latest.
    /// </summary>
    private static string? ExpectedCommitFor(IndexedAsset? entry, string? refName)
    {
        if (entry is null)
        {
            return null;
        }

        if (refName is null || string.Equals(refName, entry.Latest.Ref, StringComparison.OrdinalIgnoreCase))
        {
            return entry.Latest.Commit;
        }

        var tagged = entry.Versions.FirstOrDefault(v =>
                string.Equals(v.Tag, refName, StringComparison.OrdinalIgnoreCase))?.Commit
            ?? entry.Certified.FirstOrDefault(c =>
                string.Equals(c.Tag, refName, StringComparison.OrdinalIgnoreCase))?.Commit;
        return tagged ?? entry.Latest.Commit;
    }

    /// <summary>
    /// Clones an asset (and its resolved deps) into the global cache — used to fetch a "missing" reference.
    /// When <paramref name="solutionPath"/> is given, the fetched projects are also registered in that solution.
    /// </summary>
    public InstallResult DownloadToCache(IndexedAsset asset, IReadOnlyDictionary<string, IndexedAsset> catalog, string? solutionPath = null, string? refFolder = null)
    {
        var messages = new List<string>();
        if (!_git.IsAvailable())
        {
            return new InstallResult(false, ["git was not found on PATH."]);
        }

        try
        {
            // Fetch WHERE the broken reference points: a legacy include goes to the flat layout,
            // a versioned one to Assets/<ref>/ — and the checkout follows that ref, not blindly latest.
            var checkoutRef = string.IsNullOrEmpty(refFolder) ? asset.Latest.Ref : refFolder;
            var targetRoot = string.IsNullOrEmpty(refFolder)
                ? GlobalCacheRoot
                : Path.Combine(GlobalCacheRoot, SafeRefFolderName(refFolder));
            Directory.CreateDirectory(targetRoot);
            var folder = Clone(asset.Repo, checkoutRef, targetRoot, messages);
            VerifyHash(targetRoot, folder,
                string.Equals(checkoutRef, asset.Latest.Ref, StringComparison.Ordinal) ? asset.Latest.ContentHash : null,
                asset.Manifest.Name, messages);
            var clonedCsprojs = CsprojInspector.FindProjects(Path.Combine(targetRoot, folder, "AssetData")).Take(1).ToList();

            var missing = false;
            foreach (var depId in asset.Latest.ResolvedDependencies)
            {
                if (catalog.TryGetValue(depId, out var dep))
                {
                    var depFolder = Clone(dep.Repo, dep.Latest.Ref, targetRoot, messages);
                    VerifyHash(targetRoot, depFolder, dep.Latest.ContentHash, dep.Manifest.Name, messages);
                    clonedCsprojs.AddRange(CsprojInspector.FindProjects(Path.Combine(targetRoot, depFolder, "AssetData")).Take(1));
                }
                else
                {
                    missing = true;
                    messages.Add($"⚠ Dependency '{depId}' is not in the catalog.");
                }
            }

            AddToSolution(solutionPath, clonedCsprojs, messages);
            return new InstallResult(!missing, messages);
        }
        catch (Exception ex)
        {
            messages.Add($"✗ {ex.Message}");
            return new InstallResult(false, messages);
        }
    }

    /// <summary>
    /// Everything sitting in the shared cache, both layouts (versioned <c>Assets\&lt;ref&gt;\&lt;name&gt;</c>
    /// and legacy flat), with the status computed against the ref each clone follows.
    /// </summary>
    public IReadOnlyList<CachedAsset> ListCachedAssets(IReadOnlyDictionary<string, IndexedAsset> catalog)
    {
        var result = new List<CachedAsset>();
        if (!Directory.Exists(GlobalCacheRoot))
        {
            return result;
        }

        void Scan(string cloneRoot, string refName)
        {
            var manifestPath = Path.Combine(cloneRoot, "AssetData", "manifest.json");
            if (!Directory.Exists(Path.Combine(cloneRoot, "AssetData")))
            {
                return; // not a clone (e.g. an empty ref folder)
            }

            var manifest = File.Exists(manifestPath) ? TryReadManifest(manifestPath) : null;
            var installed = _git.ResolveCommit(cloneRoot, "HEAD") ?? "";
            IndexedAsset? entry = null;
            if (manifest is not null)
            {
                catalog.TryGetValue(manifest.Id, out entry);
            }

            var status = manifest is null ? "broken"
                : StatusOf(installed, ExpectedCommitFor(entry, string.IsNullOrEmpty(refName) ? null : refName));
            result.Add(new CachedAsset(
                manifest?.Id ?? "", manifest?.Name ?? Path.GetFileName(cloneRoot), refName, cloneRoot,
                installed, status, DirectorySize(cloneRoot)));
        }

        foreach (var top in Directory.EnumerateDirectories(GlobalCacheRoot))
        {
            if (Directory.Exists(Path.Combine(top, "AssetData")))
            {
                Scan(top, ""); // legacy flat clone
                continue;
            }

            foreach (var nested in Directory.EnumerateDirectories(top))
            {
                Scan(nested, Path.GetFileName(top)); // versioned: <ref>/<name>
            }
        }

        return result
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Ref, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static long DirectorySize(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
        }
        catch
        {
            return 0L;
        }
    }

    /// <summary>
    /// Wires an ALREADY-downloaded cache clone into target projects: ProjectReference (portable when
    /// the clone lives in the global cache) + solution registration. No network, no checkout change.
    /// Cached dependencies sitting next to the clone are registered in the solution too.
    /// </summary>
    public InstallResult AttachCached(
        string cloneRoot,
        IReadOnlyList<string> targetCsprojPaths,
        IReadOnlyDictionary<string, IndexedAsset> catalog,
        string? solutionPath = null)
    {
        var messages = new List<string>();
        if (targetCsprojPaths.Count == 0)
        {
            return new InstallResult(false, ["Select at least one target project."]);
        }

        var assetCsproj = CsprojInspector.FindProjects(Path.Combine(cloneRoot, "AssetData")).FirstOrDefault();
        if (assetCsproj is null)
        {
            return new InstallResult(false, ["No .csproj found in the cached asset's AssetData folder."]);
        }

        var inGlobalCache = cloneRoot.StartsWith(GlobalCacheRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        var globalInclude = inGlobalCache
            ? $"{GlobalCacheInclude}\\{Path.GetRelativePath(GlobalCacheRoot, assetCsproj).Replace('/', '\\')}"
            : null;

        var clonedCsprojs = new List<string> { assetCsproj };
        var manifest = TryReadManifest(Path.Combine(cloneRoot, "AssetData", "manifest.json"));
        if (manifest is not null && catalog.TryGetValue(manifest.Id, out var entry))
        {
            var refParent = Path.GetDirectoryName(cloneRoot)!;
            foreach (var depId in entry.Latest.ResolvedDependencies)
            {
                if (!catalog.TryGetValue(depId, out var dep))
                {
                    continue;
                }

                var depRoot = Path.Combine(refParent, GitClient.SafeRepoFolderName(dep.Repo));
                var depCsproj = Directory.Exists(depRoot)
                    ? CsprojInspector.FindProjects(Path.Combine(depRoot, "AssetData")).FirstOrDefault()
                    : null;
                if (depCsproj is not null)
                {
                    clonedCsprojs.Add(depCsproj);
                }
                else
                {
                    messages.Add($"⚠ Dependency '{depId}' isn't downloaded next to this clone — install it or the project won't compile.");
                }
            }
        }

        var anyTargetError = false;
        foreach (var target in targetCsprojPaths)
        {
            try
            {
                var added = globalInclude is not null
                    ? CsprojEditor.AddRawProjectReference(target, globalInclude)
                    : CsprojEditor.AddProjectReference(target, assetCsproj);
                messages.Add(added
                    ? $"✓ Added reference to {Path.GetFileName(target)}"
                    : $"• {Path.GetFileName(target)} already references the asset");
            }
            catch (Exception ex)
            {
                anyTargetError = true;
                messages.Add($"✗ {Path.GetFileName(target)}: {ex.Message}");
            }
        }

        AddToSolution(solutionPath, clonedCsprojs, messages);
        return new InstallResult(!anyTargetError, messages);
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
            // Warn when updating an existing clone actually changes the checked-out commit: in the shared
            // global cache that same folder is referenced by every project, so its version changes for all.
            var before = _git.ResolveCommit(dest, "HEAD");
            _git.UpdateToRef(dest, reference);
            var after = _git.ResolveCommit(dest, "HEAD");
            // The store root is the cache root itself for legacy clones, a subfolder otherwise.
            var rootFull = Path.GetFullPath(storeRoot);
            var shared = rootFull.Equals(GlobalCacheRoot, StringComparison.OrdinalIgnoreCase)
                || rootFull.StartsWith(GlobalCacheRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            messages.Add(before != after && shared
                ? $"⚠ Updated shared cache '{folder}' ({Short(before)}→{Short(after)}) — every project referencing it now builds this version."
                : $"• Updated {folder} ({reference})");
        }
        else
        {
            if (Directory.Exists(dest))
            {
                ForceDeleteDirectory(dest);
            }

            _git.ShallowClone(repo, reference, dest);
            messages.Add($"✓ Cloned {folder} ({reference})");
        }

        return folder;
    }

    private static string Short(string? commit) =>
        commit is { Length: >= 7 } ? commit[..7] : commit ?? "?";

    /// <summary>
    /// Adds the given projects to a .sln/.slnx under a "Store" solution folder via <c>dotnet sln add</c>,
    /// so Visual Studio loads them (a ProjectReference to a project not in the solution shows as missing).
    /// Idempotent (dotnet reports already-added projects); no-op for a lone .csproj target.
    /// </summary>
    private static void AddToSolution(string? solutionPath, IReadOnlyList<string> csprojPaths, List<string> messages)
    {
        if (string.IsNullOrWhiteSpace(solutionPath) || csprojPaths.Count == 0)
        {
            return;
        }

        var ext = Path.GetExtension(solutionPath).ToLowerInvariant();
        if (ext is not (".sln" or ".slnx"))
        {
            return; // a lone .csproj: the ProjectReference alone is enough for a CLI build
        }

        var args = new List<string> { "sln", solutionPath, "add", "--solution-folder", "Store" };
        args.AddRange(csprojPaths);

        try
        {
            var (exitCode, _, stderr) = RunDotnet(args, Path.GetDirectoryName(solutionPath));
            messages.Add(exitCode == 0
                ? $"✓ Registered {csprojPaths.Count} project(s) in {Path.GetFileName(solutionPath)} (Store folder)."
                : $"⚠ Couldn't add the projects to the solution ({Path.GetFileName(solutionPath)}) — add them manually. {stderr.Trim()}");
        }
        catch (Exception ex)
        {
            messages.Add($"⚠ Couldn't run 'dotnet sln add': {ex.Message}");
        }
    }

    private static (int ExitCode, string StdOut, string StdErr) RunDotnet(IReadOnlyList<string> args, string? workingDirectory)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("Unable to start 'dotnet'.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        // A dotnet hung on the network (NuGet restore against a dead feed) must not freeze the app.
        if (!process.WaitForExit((int)TimeSpan.FromMinutes(10).TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // process exited between the wait and the kill
            }

            process.WaitForExit(); // flush the pipe readers
            return (-1, stdout.GetAwaiter().GetResult(),
                $"dotnet {args.FirstOrDefault()} timed out after 10 minutes and was killed. {stderr.GetAwaiter().GetResult()}".TrimEnd());
        }

        return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    /// <summary>Verifies a cloned asset's AssetData/ against the content hash recorded in the index (best-effort).</summary>
    private static void VerifyHash(string storeRoot, string folder, string? expectedHash, string name, List<string> messages)
    {
        if (string.IsNullOrEmpty(expectedHash))
        {
            return;
        }

        var assetData = Path.Combine(storeRoot, folder, "AssetData");
        if (!Directory.Exists(assetData))
        {
            return;
        }

        var actual = ContentHasher.HashDirectory(assetData).Hash;
        messages.Add(string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase)
            ? $"✓ {name}: content hash verified."
            : $"⚠ {name}: content hash mismatch — the source may have changed since it was indexed.");
    }
}
