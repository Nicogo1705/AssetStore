// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

namespace AssetStore.Core.Catalog;

/// <summary>How strictly an asset's Stride version must match the target project's.</summary>
public enum StrideMatch
{
    /// <summary>Any version is accepted (no filtering).</summary>
    Any,

    /// <summary>Same major.minor (e.g. 4.2.x compatible with 4.2.y).</summary>
    Minor,

    /// <summary>Same numeric version, pre-release/build suffixes ignored (4.4.0-beta4 ≈ 4.4.0).</summary>
    Exact,

    /// <summary>Asset targets the given major.minor or newer (e.g. "≥ 4.2").</summary>
    AtLeast,

    /// <summary>Same major only (e.g. anything 4.x).</summary>
    MajorOnly,

    /// <summary>The identical version string, suffix included (4.4.0-beta4 ≠ 4.4.0-beta2).</summary>
    ExactString,
}

/// <summary>Compares detected Stride versions for compatibility filtering.</summary>
public static class StrideVersionMatcher
{
    /// <summary>
    /// True when <paramref name="assetVersion"/> is compatible with <paramref name="targetVersion"/>
    /// under the given match mode. An unknown/unparseable asset version is compatible only under
    /// the loose modes (<see cref="StrideMatch.Minor"/>, <see cref="StrideMatch.MajorOnly"/>) —
    /// the strict modes exclude it.
    /// </summary>
    public static bool IsCompatible(string? assetVersion, string targetVersion, StrideMatch match = StrideMatch.Minor)
    {
        if (match == StrideMatch.Any)
        {
            return true;
        }

        if (match == StrideMatch.ExactString)
        {
            return string.Equals(NormalizeRaw(assetVersion), NormalizeRaw(targetVersion), StringComparison.OrdinalIgnoreCase);
        }

        var asset = Parse(assetVersion);
        if (asset is null)
        {
            // Unknown version: lenient under the loose modes, excluded for Exact/AtLeast.
            return match is StrideMatch.Minor or StrideMatch.MajorOnly;
        }

        var target = Parse(targetVersion);
        if (target is null)
        {
            return true;
        }

        return match switch
        {
            // Normalized: Version.Parse("4.4") has Build=-1 and would compare unequal to "4.4.0".
            StrideMatch.Exact => Normalize(asset) == Normalize(target),
            StrideMatch.Minor => asset.Major == target.Major && asset.Minor == target.Minor,
            StrideMatch.MajorOnly => asset.Major == target.Major,
            StrideMatch.AtLeast => CompareMajorMinor(asset, target) >= 0,
            _ => true,
        };
    }

    private static string NormalizeRaw(string? value) => (value ?? "").Trim().TrimStart('v', 'V');

    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    /// <summary>Parses a Stride version (tolerates a leading 'v' and pre-release/build suffixes), or null.</summary>
    public static Version? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.TrimStart('v', 'V');
        var cut = trimmed.IndexOfAny(['-', '+']);
        if (cut >= 0)
        {
            trimmed = trimmed[..cut];
        }

        return Version.TryParse(trimmed, out var version) ? version : null;
    }

    private static int CompareMajorMinor(Version a, Version b)
    {
        var byMajor = a.Major.CompareTo(b.Major);
        return byMajor != 0 ? byMajor : a.Minor.CompareTo(b.Minor);
    }
}
