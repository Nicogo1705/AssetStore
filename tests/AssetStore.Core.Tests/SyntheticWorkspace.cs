// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Diagnostics;
using System.Text.Json;
using AssetStore.Core.Git;

namespace AssetStore.Core.Tests;

/// <summary>
/// Builds a throwaway workspace on disk so the indexing pipeline can be exercised without depending on any
/// published example repository: a container holding just a registry we populate, plus git-backed asset repos
/// we generate — including a deliberately broken one. Validation uses the real (stable) AssetContainer path,
/// so the JSON schemas are registered once globally (they cannot be re-registered under a new path). Requires
/// git; returns null otherwise.
/// </summary>
internal sealed class SyntheticWorkspace : IDisposable
{
    /// <summary>Workspace root; asset repos are created as folders directly under it.</summary>
    public string Root { get; }

    /// <summary>The synthetic container — holds only the registry we write (schemas/catalog come from the validator).</summary>
    public string Container { get; }

    private SyntheticWorkspace(string root, string container)
    {
        Root = root;
        Container = container;
    }

    public static SyntheticWorkspace? TryCreate()
    {
        if (!new GitClient().IsAvailable())
        {
            return null;
        }

        var root = Directory.CreateTempSubdirectory("as-ws-").FullName;
        var container = Path.Combine(root, "AssetContainer");
        Directory.CreateDirectory(Path.Combine(container, "registry"));
        return new SyntheticWorkspace(root, container);
    }

    public static string RepoUrl(string repoName) => $"https://github.com/test/{repoName}";

    /// <summary>A minimal valid manifest (add <paramref name="deps"/> to declare store dependencies).</summary>
    public static string Manifest(string id, string name, string category, string[]? deps = null)
    {
        var dependencies = deps is null ? "" : $""","dependencies": [{string.Join(", ", deps.Select(d => $"\"{d}\""))}]""";
        return $$"""
            {
              "schemaVersion": 1,
              "id": "{{id}}",
              "name": "{{name}}",
              "authors": [{ "name": "Tester" }],
              "description": "A {{name}} asset.",
              "category": "{{category}}",
              "license": "MIT"{{dependencies}}
            }
            """;
    }

    /// <summary>A minimal Stride .csproj; <c>&lt;!--REF--&gt;</c> is where a ProjectReference is injected.</summary>
    public static string Csproj() => """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
          <ItemGroup><PackageReference Include="Stride.Engine" Version="4.2.0.1" /></ItemGroup>
          <!--REF-->
        </Project>
        """;

    /// <summary>
    /// Creates a git-backed asset repo (folder <paramref name="repoName"/> with an <c>AssetData/</c> folder
    /// holding the manifest and an optional .csproj) and registers it in the container's registry.
    /// </summary>
    public void AddAsset(
        string repoName,
        string manifestJson,
        string? csprojXml = null,
        string? projectReferenceToRepo = null)
    {
        var repo = Path.Combine(Root, repoName);
        var assetData = Path.Combine(repo, "AssetData");
        Directory.CreateDirectory(assetData);
        File.WriteAllText(Path.Combine(assetData, "manifest.json"), manifestJson);

        if (csprojXml is not null)
        {
            var xml = projectReferenceToRepo is null
                ? csprojXml
                : csprojXml.Replace(
                    "<!--REF-->",
                    $"""<ItemGroup><ProjectReference Include="..\..\{projectReferenceToRepo}\AssetData\{projectReferenceToRepo}.csproj" /></ItemGroup>""");
            File.WriteAllText(Path.Combine(assetData, $"{repoName}.csproj"), xml);
        }

        InitRepo(repo);

        var id = JsonDocument.Parse(manifestJson).RootElement.GetProperty("id").GetString()!;
        var registry = $$"""
            {
              "id": "{{id}}",
              "repo": "{{RepoUrl(repoName)}}",
              "latest": { "ref": "main" }
            }
            """;
        File.WriteAllText(Path.Combine(Container, "registry", $"{id}.json"), registry);
    }

    private static void InitRepo(string dir)
    {
        Git(dir, "init", "-b", "main");
        Git(dir, "add", "-A");
        Git(dir, "-c", "user.email=test@example.com", "-c", "user.name=Test",
            "-c", "commit.gpgsign=false", "commit", "-m", "init");
    }

    private static void Git(string workingDir, params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start git.");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {process.StandardError.ReadToEnd()}");
        }
    }

    public void Dispose()
    {
        try
        {
            // git marks pack/object files read-only on Windows — clear before deleting.
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                var attributes = File.GetAttributes(file);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
            }

            Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // best-effort temp cleanup
        }
    }
}
