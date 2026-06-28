// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

namespace AssetStore.Core.Models;

/// <summary>
/// Strongly-typed view of <c>AssetData/manifest.json</c>.
/// See <c>schemas/manifest.schema.json</c> in the AssetContainer repository.
/// </summary>
public sealed record AssetManifest
{
    public int SchemaVersion { get; init; } = 1;

    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Version { get; init; }

    public IReadOnlyList<Author> Authors { get; init; } = [];

    public required string Description { get; init; }

    public required string Category { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public required string License { get; init; }

    /// <summary>Optional override. By default the Stride version is detected from the .csproj.</summary>
    public string? StrideVersion { get; init; }

    public string? Thumbnail { get; init; }

    public IReadOnlyList<string> Screenshots { get; init; } = [];

    public string? PreviewVideo { get; init; }

    /// <summary>Ids of other store assets this asset depends on (no version constraint).</summary>
    public IReadOnlyList<string> Dependencies { get; init; } = [];

    public string? Repository { get; init; }

    /// <summary>Suggested import mode: "local" (clone source) or "nuget" (PackageReference). Defaults to local.</summary>
    public string? DefaultImport { get; init; }

    /// <summary>Optional NuGet publication; enables nugetImport when present.</summary>
    public NugetPackage? Nuget { get; init; }
}
