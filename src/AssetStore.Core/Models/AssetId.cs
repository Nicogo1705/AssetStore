// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Text.RegularExpressions;

namespace AssetStore.Core.Models;

/// <summary>The reverse-DNS asset id format, shared by the publish form, the manifest generator and the schemas.</summary>
public static partial class AssetId
{
    /// <summary>Pattern for a reverse-DNS id, e.g. <c>com.author.cool-asset</c>. Mirrors manifest.schema.json.</summary>
    public const string Pattern = "^[a-z0-9]+(\\.[a-z0-9-]+)+$";

    [GeneratedRegex(Pattern)]
    private static partial Regex Matcher();

    /// <summary>True if <paramref name="id"/> is a valid reverse-DNS asset id.</summary>
    public static bool IsValid(string? id) => !string.IsNullOrEmpty(id) && Matcher().IsMatch(id);
}
