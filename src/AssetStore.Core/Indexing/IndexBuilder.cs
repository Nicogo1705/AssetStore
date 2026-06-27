// Copyright (c) Stride contributors (https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

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
    Func<string, int?>? starsProvider = null)
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
        Version = "0.0.0",
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
