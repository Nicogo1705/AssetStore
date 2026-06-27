// Copyright (c) Stride contributors (https://stride3d.net)
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
}

/// <summary>Compares detected Stride versions for compatibility filtering.</summary>
public static class StrideVersionMatcher
{
    /// <summary>
    /// True when <paramref name="assetVersion"/> is compatible with <paramref name="targetVersion"/>
    /// under the given match mode. Unknown/unparseable asset versions are compatible unless the mode
    /// is <see cref="StrideMatch.Exact"/>.
    /// </summary>
    public static bool IsCompatible(string? assetVersion, string targetVersion, StrideMatch match = StrideMatch.Minor)
    {
        if (match == StrideMatch.Any)
        {
            return true;
        }

        if (!TryParse(assetVersion, out var asset))
        {
            return match != StrideMatch.Exact;
        }

        if (!TryParse(targetVersion, out var target))
        {
            return true;
        }

        return match switch
        {
            StrideMatch.Exact => asset == target,
            StrideMatch.Minor => asset.Major == target.Major && asset.Minor == target.Minor,
            _ => true,
        };
    }

    private static bool TryParse(string? value, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        // Strip a leading 'v' and any pre-release/build suffix (after '-' or '+').
        var trimmed = value.TrimStart('v', 'V');
        var cut = trimmed.IndexOfAny(['-', '+']);
        if (cut >= 0)
        {
            trimmed = trimmed[..cut];
        }

        return Version.TryParse(trimmed, out version!);
    }
}
