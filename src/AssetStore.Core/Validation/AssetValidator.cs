// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Text.Json;
using AssetStore.Core.Models;
using AssetStore.Core.Serialization;

namespace AssetStore.Core.Validation;

/// <summary>Validates registry entries and manifests against schemas and catalog rules.</summary>
public sealed class AssetValidator
{
    private readonly SchemaValidator _registrySchema;
    private readonly SchemaValidator _manifestSchema;
    private readonly Catalog _catalog;

    public AssetValidator(SchemaValidator registrySchema, SchemaValidator manifestSchema, Catalog catalog)
    {
        _registrySchema = registrySchema;
        _manifestSchema = manifestSchema;
        _catalog = catalog;
    }

    /// <summary>Builds a validator from an AssetContainer checkout (schemas/ and catalog/ folders).</summary>
    public static AssetValidator FromContainer(string containerRoot) => new(
        SchemaValidator.FromFile(Path.Combine(containerRoot, "schemas", "registry-entry.schema.json")),
        SchemaValidator.FromFile(Path.Combine(containerRoot, "schemas", "manifest.schema.json")),
        Catalog.Load(Path.Combine(containerRoot, "catalog")));

    /// <summary>Schema-validates a registry file and checks that its id matches the file name.</summary>
    public RegistryEntry? ValidateRegistryFile(string path, ValidationReport report)
    {
        var json = File.ReadAllText(path);
        if (_registrySchema.Validate(json, report, "registry") is null || report.HasErrors)
        {
            return null;
        }

        var entry = TryDeserialize<RegistryEntry>(json, "registry", report);
        if (entry is null)
        {
            return null;
        }

        var expectedId = Path.GetFileNameWithoutExtension(path);
        if (!string.Equals(entry.Id, expectedId, StringComparison.Ordinal))
        {
            report.Error("id.filename", $"Entry id '{entry.Id}' does not match file name '{expectedId}'.");
        }

        // Only allow https git remotes: blocks ssh/file/ext:: transports (the latter is git RCE).
        if (!entry.Repo.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            report.Error("repo.scheme", $"Repository URL must start with https:// (got '{entry.Repo}').");
        }

        return entry;
    }

    /// <summary>Schema-validates a manifest and checks category/license against the catalog.</summary>
    public AssetManifest? ValidateManifest(string assetDataPath, ValidationReport report)
    {
        var manifestPath = Path.Combine(assetDataPath, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            report.Error("manifest.missing", $"No manifest.json found in '{assetDataPath}'.");
            return null;
        }

        var json = File.ReadAllText(manifestPath);
        if (_manifestSchema.Validate(json, report, "manifest") is null || report.HasErrors)
        {
            return null;
        }

        var manifest = TryDeserialize<AssetManifest>(json, "manifest", report);
        if (manifest is null)
        {
            return null;
        }

        if (!_catalog.Categories.Contains(manifest.Category))
        {
            report.Error("category.unknown", $"Category '{manifest.Category}' is not in catalog/categories.json.");
        }

        if (!_catalog.Licenses.Contains(manifest.License))
        {
            report.Error("license.unknown", $"License '{manifest.License}' is not in catalog/licenses.json.");
        }

        if (string.Equals(manifest.DefaultImport, "nuget", StringComparison.Ordinal) && manifest.Nuget is null)
        {
            report.Error("nuget.missing", "defaultImport is 'nuget' but no 'nuget' package block is declared.");
        }

        return manifest;
    }

    /// <summary>Cross-checks that an entry and its manifest agree on the asset id.</summary>
    public static void CheckEntryManifestConsistency(RegistryEntry entry, AssetManifest manifest, ValidationReport report)
    {
        if (!string.Equals(entry.Id, manifest.Id, StringComparison.Ordinal))
        {
            report.Error("id.mismatch", $"Registry id '{entry.Id}' differs from manifest id '{manifest.Id}'.");
        }
    }

    private static T? TryDeserialize<T>(string json, string code, ValidationReport report)
        where T : class
    {
        try
        {
            return AssetStoreJson.Deserialize<T>(json);
        }
        catch (JsonException ex)
        {
            report.Error($"{code}.deserialize", ex.Message);
            return null;
        }
    }
}
