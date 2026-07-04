// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using AssetStore.Core.Projects;

namespace AssetStore.Core.Tests;

public sealed class CsprojInspectorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("csproj-").FullName;

    [Fact]
    public void Detects_stride_version_preferring_engine()
    {
        var path = Write("game.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Stride.Physics" Version="4.1.0.0" />
                <PackageReference Include="Stride.Engine" Version="4.2.0.1" />
              </ItemGroup>
            </Project>
            """);

        Assert.Equal("4.2.0.1", CsprojInspector.DetectStrideVersion(path));
    }

    [Fact]
    public void Returns_null_when_no_stride_reference()
    {
        var path = Write("plain.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="13.0.0" />
              </ItemGroup>
            </Project>
            """);

        Assert.Null(CsprojInspector.DetectStrideVersion(path));
    }

    [Fact]
    public void Reads_project_references()
    {
        var path = Write("dependent.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="..\Lib\Lib.csproj" />
              </ItemGroup>
            </Project>
            """);

        var references = CsprojInspector.GetProjectReferences(path);

        Assert.Single(references);
        Assert.Equal(@"..\Lib\Lib.csproj", references[0]);
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
