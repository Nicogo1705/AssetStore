// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Text.Json;

namespace AssetStore.Core.Validation;

/// <summary>Allowed categories and licenses, loaded from the AssetContainer <c>catalog/</c> folder.</summary>
public sealed class Catalog
{
    public required IReadOnlySet<string> Categories { get; init; }

    public required IReadOnlySet<string> Licenses { get; init; }

    /// <summary>Loads <c>categories.json</c> and <c>licenses.json</c> from a catalog directory.</summary>
    public static Catalog Load(string catalogDirectory)
    {
        var categories = ReadIds(Path.Combine(catalogDirectory, "categories.json"), "categories", "id");
        var licenses = ReadStrings(Path.Combine(catalogDirectory, "licenses.json"), "licenses");
        return new Catalog
        {
            Categories = categories.ToHashSet(StringComparer.Ordinal),
            Licenses = licenses.ToHashSet(StringComparer.Ordinal),
        };
    }

    private static IEnumerable<string> ReadIds(string path, string arrayName, string idField)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var item in doc.RootElement.GetProperty(arrayName).EnumerateArray())
        {
            yield return item.GetProperty(idField).GetString()!;
        }
    }

    private static IEnumerable<string> ReadStrings(string path, string arrayName)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var item in doc.RootElement.GetProperty(arrayName).EnumerateArray())
        {
            yield return item.GetString()!;
        }
    }
}
