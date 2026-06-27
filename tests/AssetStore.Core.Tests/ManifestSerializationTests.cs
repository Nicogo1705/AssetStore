// Copyright (c) Stride contributors (https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using AssetStore.Core.Models;
using AssetStore.Core.Serialization;

namespace AssetStore.Core.Tests;

public sealed class ManifestSerializationTests
{
    [Fact]
    public void Parses_nuget_and_import_mode()
    {
        var manifest = AssetStoreJson.Deserialize<AssetManifest>("""
            {
              "schemaVersion": 1,
              "id": "com.test.widget",
              "name": "Widget",
              "version": "1.0.0",
              "authors": [{ "name": "Tester" }],
              "description": "A test widget.",
              "category": "Scripts",
              "license": "MIT",
              "defaultImport": "nuget",
              "nuget": { "packageId": "Tester.Widget", "packageVersion": "1.0.0" }
            }
            """);

        Assert.Equal("nuget", manifest.DefaultImport);
        Assert.NotNull(manifest.Nuget);
        Assert.Equal("Tester.Widget", manifest.Nuget!.PackageId);
        Assert.Equal("1.0.0", manifest.Nuget.PackageVersion);
    }

    [Fact]
    public void Nuget_is_absent_by_default()
    {
        var manifest = AssetStoreJson.Deserialize<AssetManifest>("""
            {
              "schemaVersion": 1,
              "id": "com.test.widget",
              "name": "Widget",
              "version": "1.0.0",
              "authors": [{ "name": "Tester" }],
              "description": "A test widget.",
              "category": "Scripts",
              "license": "MIT"
            }
            """);

        Assert.Null(manifest.Nuget);
        Assert.Null(manifest.DefaultImport);
    }
}
