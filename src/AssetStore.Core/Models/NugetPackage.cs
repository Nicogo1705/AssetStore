// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

namespace AssetStore.Core.Models;

/// <summary>
/// Optional NuGet publication of an asset. When present, the asset can be imported as a
/// <c>PackageReference</c> (nugetImport) in addition to the always-available source import
/// (localImport: clone + ProjectReference).
/// </summary>
public sealed record NugetPackage
{
    public required string PackageId { get; init; }

    /// <summary>Optional pinned/suggested version; the app may otherwise resolve the latest.</summary>
    public string? PackageVersion { get; init; }
}
