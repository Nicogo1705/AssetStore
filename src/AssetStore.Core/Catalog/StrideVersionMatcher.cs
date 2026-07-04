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

    /// <summary>Exactly the same version.</summary>
    Exact,

    /// <summary>Asset targets the given major.minor or newer (e.g. "≥ 4.2").</summary>
    AtLeast,
}

/// <summary>Compares detected Stride versions for compatibility filtering.</summary>
public static class StrideVersionMatcher
{
    /// <summary>
    /// True when <paramref name="assetVersion"/> is compatible with <paramref name="targetVersion"/>
    /// under the given match mode. An unknown/unparseable asset version is compatible only under
    /// <see cref="StrideMatch.Minor"/> (it is excluded by <see cref="StrideMatch.Exact"/> and
    /// <see cref="StrideMatch.AtLeast"/>).
    /// </summary>
    public static bool IsCompatible(string? assetVersion, string targetVersion, StrideMatch match = StrideMatch.Minor)
    {
        if (match == StrideMatch.Any)
        {
            return true;
        }

        var asset = Parse(assetVersion);
        if (asset is null)
        {
            // Unknown version: lenient under Minor, excluded for Exact/AtLeast.
            return match == StrideMatch.Minor;
        }

        var target = Parse(targetVersion);
        if (target is null)
        {
            return true;
        }

        return match switch
        {
            StrideMatch.Exact => asset == target,
            StrideMatch.Minor => asset.Major == target.Major && asset.Minor == target.Minor,
            StrideMatch.AtLeast => CompareMajorMinor(asset, target) >= 0,
            _ => true,
        };
    }

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
