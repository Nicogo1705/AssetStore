// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using AssetStore.Core.Validation;

namespace AssetStore.Core.Tests;

public sealed class AssetValidatorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("validator-").FullName;

    [Fact]
    public void Accepts_a_well_formed_manifest()
    {
        if (!TestPaths.Available)
        {
            return;
        }

        var assetData = WriteManifest("""
            {
              "schemaVersion": 1,
              "id": "com.test.widget",
              "name": "Widget",
              "authors": [{ "name": "Tester" }],
              "description": "A test widget.",
              "category": "Scripts",
              "license": "MIT"
            }
            """);

        var report = new ValidationReport();
        var manifest = AssetValidator.FromContainer(TestPaths.Container).ValidateManifest(assetData, report);

        Assert.NotNull(manifest);
        Assert.False(report.HasErrors);
    }

    [Fact]
    public void Rejects_an_unknown_category()
    {
        if (!TestPaths.Available)
        {
            return;
        }

        var assetData = WriteManifest("""
            {
              "schemaVersion": 1,
              "id": "com.test.widget",
              "name": "Widget",
              "authors": [{ "name": "Tester" }],
              "description": "A test widget.",
              "category": "NotARealCategory",
              "license": "MIT"
            }
            """);

        var report = new ValidationReport();
        AssetValidator.FromContainer(TestPaths.Container).ValidateManifest(assetData, report);

        Assert.True(report.HasErrors);
        Assert.Contains(report.Messages, m => m.Code == "category.unknown");
    }

    private string WriteManifest(string json)
    {
        var assetData = Path.Combine(_dir, Guid.NewGuid().ToString("N"), "AssetData");
        Directory.CreateDirectory(assetData);
        File.WriteAllText(Path.Combine(assetData, "manifest.json"), json);
        return assetData;
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
