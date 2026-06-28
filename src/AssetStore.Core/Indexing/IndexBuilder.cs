// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using AssetStore.Core.Catalog;
using AssetStore.Core.Dependencies;
using AssetStore.Core.Hashing;
using AssetStore.Core.Models;
using AssetStore.Core.Projects;
using AssetStore.Core.Validation;

namespace AssetStore.Core.Indexing;

/// <summary>
/// Crawls the AssetContainer registry, validates and enriches every entry, and produces the
/// aggregated <see cref="IndexLock"/> consumed by the app.
/// </summary>
public sealed class IndexBuilder(
    string containerRoot,
    IAssetSource source,
    AssetValidator validator,
    Func<string, int?>? starsProvider = null,
    Func<string, IReadOnlyList<(string Tag, string Commit)>>? tagsProvider = null,
    Func<string, string, string?>? headProvider = null)
{
    private const string UnresolvedCommit = "0000000000000000000000000000000000000000";

    /// <summary>Builds the index. <paramref name="generatedAt"/> is an ISO-8601 timestamp (caller-supplied).</summary>
    public IndexLock Build(string generatedAt)
    {
        var registryDir = Path.Combine(containerRoot, "registry");
        var contexts = new List<AssetContext>();

        // Pass 1 — load and validate every entry + manifest, materialize each checkout.
        foreach (var file in Directory.EnumerateFiles(registryDir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            var report = new ValidationReport();
            var entry = validator.ValidateRegistryFile(file, report);
            if (entry is null)
            {
                contexts.Add(AssetContext.Failed(Path.GetFileNameWithoutExtension(file), report));
                continue;
            }

            AssetCheckout checkout;
            try
            {
                checkout = source.Fetch(entry);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                report.Error("source.fetch", ex.Message);
                contexts.Add(AssetContext.Unavailable(entry, report));
                continue;
            }

            var manifest = validator.ValidateManifest(checkout.AssetDataPath, report);
            if (manifest is not null)
            {
                AssetValidator.CheckEntryManifestConsistency(entry, manifest, report);
            }

            contexts.Add(new AssetContext(entry.Id, entry, checkout, manifest, report));
        }

        var csprojToId = BuildProjectIndex(contexts);

        // Dependencies are derived automatically from each project's <ProjectReference> entries
        // (mapped to store ids), unioned with any explicitly declared manifest dependencies.
        var directDeps = contexts
            .Where(c => c.Manifest is not null && c.Checkout is not null)
            .ToDictionary(
                c => c.Id,
                c => (IReadOnlyList<string>)ProjectRefIds(c, csprojToId)
                    .Union(c.Manifest!.Dependencies, StringComparer.Ordinal)
                    .Distinct(StringComparer.Ordinal)
                    .ToList(),
                StringComparer.Ordinal);

        // Pass 2 — enrich each loadable asset and assemble the index entries.
        var assets = new List<IndexedAsset>();
        foreach (var ctx in contexts)
        {
            if (ctx.Entry is null || ctx.Manifest is null || ctx.Checkout is null)
            {
                if (ctx.Entry is not null)
                {
                    assets.Add(Unavailable(ctx));
                }

                continue;
            }

            assets.Add(BuildAsset(ctx, csprojToId, directDeps, generatedAt));
        }

        return new IndexLock { GeneratedAt = generatedAt, Assets = assets };
    }

    private IndexedAsset BuildAsset(
        AssetContext ctx,
        IReadOnlyDictionary<string, string> csprojToId,
        IReadOnlyDictionary<string, IReadOnlyList<string>> directDeps,
        string generatedAt)
    {
        var report = ctx.Report;
        var entry = ctx.Entry!;
        var manifest = ctx.Manifest!;
        var checkout = ctx.Checkout!;

        var hash = ContentHasher.HashDirectory(checkout.AssetDataPath);
        var strideVersion = manifest.StrideVersion ?? DetectStrideVersion(checkout.AssetDataPath);
        if (strideVersion is null)
        {
            report.Warning("stride.undetected", "Could not detect a Stride version from any .csproj.");
        }

        var resolution = DependencyResolver.Resolve(entry.Id, directDeps);
        if (resolution.HasCycle)
        {
            report.Error("deps.cycle", $"Dependency cycle: {string.Join(" -> ", resolution.Cycle!)}.");
        }

        foreach (var missing in resolution.Missing)
        {
            report.Error("deps.missing", $"Dependency '{missing}' is not present in the registry.");
        }

        var commit = checkout.Commit;
        if (commit is null)
        {
            report.Warning("commit.unresolved", "Commit could not be resolved (git unavailable); using placeholder.");
        }

        return new IndexedAsset
        {
            Id = entry.Id,
            Repo = entry.Repo,
            Manifest = manifest,
            Stars = starsProvider?.Invoke(entry.Repo),
            Versions = BuildVersions(entry.Repo),
            Certified = MapCertified(entry),
            Latest = new IndexedVersion
            {
                Ref = entry.Latest.Ref,
                Commit = commit ?? UnresolvedCommit,
                ContentHash = hash.Hash,
                DetectedStrideVersion = strideVersion,
                ResolvedDependencies = resolution.Dependencies,
                SizeBytes = hash.TotalBytes,
                Validated = !report.HasErrors,
            },
            ValidationStatus = report.Status,
            ValidationMessages = report.Messages.Select(m => m.ToString()).ToList(),
            LastValidatedAt = generatedAt,
        };
    }

    private static IReadOnlyList<IndexedCertifiedVersion> MapCertified(RegistryEntry entry) =>
        entry.Certified.Select(c => new IndexedCertifiedVersion
        {
            Version = c.Version,
            Tag = c.Tag,
            Commit = c.Commit,
            CertifiedBy = c.CertifiedBy,
            CertifiedAt = c.CertifiedAt,
        }).ToList();

    private IReadOnlyList<IndexedTagVersion> BuildVersions(string repo) =>
        (tagsProvider?.Invoke(repo) ?? [])
            .Select(t => new IndexedTagVersion
            {
                Tag = t.Tag,
                Commit = t.Commit,
                Version = t.Tag.TrimStart('v', 'V'),
            })
            .OrderByDescending(t => StrideVersionMatcher.Parse(t.Version) ?? new Version(0, 0))
            .ToList();

    /// <summary>
    /// Incremental rebuild: only assets whose tracked ref moved (detected via <c>headProvider</c>,
    /// i.e. ls-remote — no clone) are re-fetched and reprocessed. Unchanged assets are reused from
    /// <paramref name="previous"/> with just their stars and versions refreshed. Dependencies are
    /// mapped clone-free by matching ProjectReference path segments to registry repo folder names.
    /// </summary>
    public IndexLock BuildIncremental(IndexLock? previous, string generatedAt)
    {
        var registryDir = Path.Combine(containerRoot, "registry");
        var prevById = previous?.Assets.ToDictionary(a => a.Id, StringComparer.Ordinal)
            ?? new Dictionary<string, IndexedAsset>(StringComparer.Ordinal);
        var folderToId = BuildFolderToId(registryDir);

        var reused = new Dictionary<string, IndexedAsset>(StringComparer.Ordinal);
        var toRebuild = new List<RegistryEntry>();

        foreach (var file in Directory.EnumerateFiles(registryDir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            var report = new ValidationReport();
            var entry = validator.ValidateRegistryFile(file, report);
            if (entry is null)
            {
                continue;
            }

            var head = headProvider?.Invoke(entry.Repo, entry.Latest.Ref);
            if (prevById.TryGetValue(entry.Id, out var prev) && head is not null && prev.Latest.Commit == head)
            {
                reused[entry.Id] = prev with
                {
                    Repo = entry.Repo,
                    Stars = starsProvider?.Invoke(entry.Repo) ?? prev.Stars,
                    Versions = BuildVersions(entry.Repo),
                    LastValidatedAt = generatedAt,
                };
            }
            else
            {
                toRebuild.Add(entry);
            }
        }

        // Direct-dependency edges for the resolver: rebuilt assets from their ProjectReferences,
        // reused assets from their already-resolved (transitive) set.
        var rebuilt = ReprocessAssets(toRebuild, folderToId, generatedAt, out var rebuiltDirectDeps);
        var directDeps = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (id, a) in reused)
        {
            directDeps[id] = a.Latest.ResolvedDependencies;
        }

        foreach (var (id, deps) in rebuiltDirectDeps)
        {
            directDeps[id] = deps;
        }

        // Finalize resolved dependencies for ALL assets now that every edge is known — including
        // reused assets, whose transitive set can change when one of their dependencies changed.
        var assets = new List<IndexedAsset>();
        foreach (var asset in reused.Values.Concat(rebuilt))
        {
            var resolution = DependencyResolver.Resolve(asset.Id, directDeps);
            assets.Add(asset with { Latest = asset.Latest with { ResolvedDependencies = resolution.Dependencies } });
        }

        return new IndexLock
        {
            GeneratedAt = generatedAt,
            Assets = assets.OrderBy(a => a.Id, StringComparer.Ordinal).ToList(),
        };
    }

    private List<IndexedAsset> ReprocessAssets(
        IReadOnlyList<RegistryEntry> entries,
        IReadOnlyDictionary<string, string> folderToId,
        string generatedAt,
        out Dictionary<string, IReadOnlyList<string>> directDeps)
    {
        directDeps = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var result = new List<IndexedAsset>();

        foreach (var entry in entries)
        {
            var report = new ValidationReport();
            AssetCheckout checkout;
            try
            {
                checkout = source.Fetch(entry);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                report.Error("source.fetch", ex.Message);
                result.Add(Unavailable(new AssetContext(entry.Id, entry, null, null, report)));
                continue;
            }

            var manifest = validator.ValidateManifest(checkout.AssetDataPath, report);
            if (manifest is null)
            {
                result.Add(Unavailable(new AssetContext(entry.Id, entry, checkout, null, report)));
                continue;
            }

            AssetValidator.CheckEntryManifestConsistency(entry, manifest, report);

            var direct = ProjectRefIdsByFolder(checkout, folderToId, entry.Id)
                .Union(manifest.Dependencies, StringComparer.Ordinal)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            directDeps[entry.Id] = direct;

            var hash = ContentHasher.HashDirectory(checkout.AssetDataPath);
            var strideVersion = manifest.StrideVersion ?? DetectStrideVersion(checkout.AssetDataPath);
            var commit = checkout.Commit ?? UnresolvedCommit;

            result.Add(new IndexedAsset
            {
                Id = entry.Id,
                Repo = entry.Repo,
                Manifest = manifest,
                Stars = starsProvider?.Invoke(entry.Repo),
                Versions = BuildVersions(entry.Repo),
                Certified = MapCertified(entry),
                Latest = new IndexedVersion
                {
                    Ref = entry.Latest.Ref,
                    Commit = commit,
                    ContentHash = hash.Hash,
                    DetectedStrideVersion = strideVersion,
                    ResolvedDependencies = direct, // replaced with transitive set by the caller
                    SizeBytes = hash.TotalBytes,
                    Validated = !report.HasErrors,
                },
                ValidationStatus = report.Status,
                ValidationMessages = report.Messages.Select(m => m.ToString()).ToList(),
                LastValidatedAt = generatedAt,
            });
        }

        return result;
    }

    /// <summary>Maps each registry repo's folder name (URL last segment) to its asset id, without cloning.</summary>
    private static Dictionary<string, string> BuildFolderToId(string registryDir)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(registryDir, "*.json"))
        {
            try
            {
                var entry = Serialization.AssetStoreJson.Deserialize<RegistryEntry>(File.ReadAllText(file));
                var folder = entry.Repo.TrimEnd('/').Split('/').Last();
                if (folder.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                {
                    folder = folder[..^4];
                }

                map[folder] = entry.Id;
            }
            catch
            {
                // skip malformed entry; full validation happens elsewhere
            }
        }

        return map;
    }

    /// <summary>Store dep ids referenced by an asset's ProjectReferences, matched by folder name (clone-free).</summary>
    private static IEnumerable<string> ProjectRefIdsByFolder(
        AssetCheckout checkout,
        IReadOnlyDictionary<string, string> folderToId,
        string selfId)
    {
        foreach (var csproj in CsprojInspector.FindProjects(checkout.AssetDataPath))
        {
            foreach (var reference in CsprojInspector.GetProjectReferences(csproj))
            {
                // Match only "<repoFolder>/AssetData/..." so a same-named unrelated folder can't false-match.
                var parts = reference.Split('/', '\\');
                for (var i = 0; i < parts.Length - 1; i++)
                {
                    if (parts[i + 1].Equals("AssetData", StringComparison.OrdinalIgnoreCase)
                        && folderToId.TryGetValue(parts[i], out var id)
                        && !string.Equals(id, selfId, StringComparison.Ordinal))
                    {
                        yield return id;
                    }
                }
            }
        }
    }

    /// <summary>Maps every project file (full path) found in any checkout to its owning asset id.</summary>
    private static Dictionary<string, string> BuildProjectIndex(IEnumerable<AssetContext> contexts)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ctx in contexts)
        {
            if (ctx.Checkout is null)
            {
                continue;
            }

            foreach (var csproj in CsprojInspector.FindProjects(ctx.Checkout.AssetDataPath))
            {
                map[Path.GetFullPath(csproj)] = ctx.Id;
            }
        }

        return map;
    }

    /// <summary>Store asset ids referenced by this asset's projects via &lt;ProjectReference&gt;.</summary>
    private static IEnumerable<string> ProjectRefIds(AssetContext ctx, IReadOnlyDictionary<string, string> csprojToId)
    {
        foreach (var csproj in CsprojInspector.FindProjects(ctx.Checkout!.AssetDataPath))
        {
            var projectDir = Path.GetDirectoryName(Path.GetFullPath(csproj))!;
            foreach (var reference in CsprojInspector.GetProjectReferences(csproj))
            {
                var referencedPath = Path.GetFullPath(
                    Path.Combine(projectDir, reference.Replace('\\', Path.DirectorySeparatorChar)));
                if (csprojToId.TryGetValue(referencedPath, out var referencedId)
                    && !string.Equals(referencedId, ctx.Id, StringComparison.Ordinal))
                {
                    yield return referencedId;
                }
            }
        }
    }

    private static string? DetectStrideVersion(string assetDataPath)
    {
        foreach (var csproj in CsprojInspector.FindProjects(assetDataPath))
        {
            var version = CsprojInspector.DetectStrideVersion(csproj);
            if (version is not null)
            {
                return version;
            }
        }

        return null;
    }

    private static IndexedAsset Unavailable(AssetContext ctx) => new()
    {
        Id = ctx.Id,
        Repo = ctx.Entry!.Repo,
        Manifest = ctx.Manifest ?? PlaceholderManifest(ctx.Id),
        Latest = new IndexedVersion
        {
            Ref = ctx.Entry.Latest.Ref,
            Commit = ctx.Checkout?.Commit ?? UnresolvedCommit,
            ContentHash = string.Empty,
            Validated = false,
        },
        ValidationStatus = "unavailable",
        ValidationMessages = ctx.Report.Messages.Select(m => m.ToString()).ToList(),
    };

    private static AssetManifest PlaceholderManifest(string id) => new()
    {
        Id = id,
        Name = id,
        Description = "(unavailable)",
        Category = "Other",
        License = "MIT",
    };

    private sealed record AssetContext(
        string Id,
        RegistryEntry? Entry,
        AssetCheckout? Checkout,
        AssetManifest? Manifest,
        ValidationReport Report)
    {
        public static AssetContext Failed(string id, ValidationReport report) =>
            new(id, null, null, null, report);

        public static AssetContext Unavailable(RegistryEntry entry, ValidationReport report) =>
            new(entry.Id, entry, null, null, report);
    }
}
