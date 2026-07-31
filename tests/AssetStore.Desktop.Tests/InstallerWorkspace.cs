// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Diagnostics;
using AssetStore.Core.Models;
using AssetStore.Desktop.Services;

namespace AssetStore.Desktop.Tests;

/// <summary>
/// A disposable synthetic machine for <c>DesktopInstaller</c> tests: real (tiny) git repos as
/// asset clones, real .sln/.csproj files, and the per-machine global cache redirected into the
/// workspace via <c>DesktopInstaller.AppDataOverride</c> (on Windows,
/// <c>GetFolderPath(ApplicationData)</c> uses the shell API and ignores the APPDATA variable, so
/// an environment redirect wouldn't take). The override drives both <c>GlobalCacheRoot</c> and
/// the MSBuild marker expansion, so they stay coherent. No network anywhere.
/// </summary>
public sealed class InstallerWorkspace : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("installer-").FullName;

    /// <summary>The redirected app-data folder (the cache root lives under it).</summary>
    public string AppData { get; }

    public InstallerWorkspace()
    {
        AppData = Path.Combine(Root, "appdata");
        Directory.CreateDirectory(AppData);
        DesktopInstaller.AppDataOverride = AppData;
    }

    public string CacheRoot => Path.Combine(AppData, "StrideAssetStore", "Assets");

    /// <summary>
    /// Creates a store-asset clone (git repo with <c>AssetData/manifest.json</c> and a library
    /// csproj) at <paramref name="relativePath"/> under <see cref="Root"/>, and returns its
    /// absolute path plus HEAD commit.
    /// </summary>
    public (string CloneRoot, string Head) CreateAssetClone(
        string relativePath, string id, string name, string strideVersion = "4.4.0.2")
    {
        var cloneRoot = Path.Combine(Root, relativePath);
        var lib = Path.Combine(cloneRoot, "AssetData", name);
        Directory.CreateDirectory(lib);

        File.WriteAllText(Path.Combine(cloneRoot, "AssetData", "manifest.json"), $$"""
            {
              "schemaVersion": 1,
              "id": "{{id}}",
              "name": "{{name}}",
              "description": "Synthetic test asset.",
              "category": "Scripts",
              "license": "MIT"
            }
            """);
        File.WriteAllText(Path.Combine(lib, $"{name}.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Stride.Engine" Version="{strideVersion}" />
              </ItemGroup>
            </Project>
            """);

        Git(cloneRoot, "init", "-q");
        Git(cloneRoot, "-c", "user.email=t@t", "-c", "user.name=t", "add", "-A");
        Git(cloneRoot, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-q", "-m", "init");
        return (cloneRoot, Git(cloneRoot, "rev-parse", "HEAD").Trim());
    }

    /// <summary>Creates a game .csproj at <paramref name="relativePath"/>.</summary>
    public string CreateGameProject(string relativePath, string strideVersion = "4.4.0.2")
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Stride.Engine" Version="{strideVersion}" />
                <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
              </ItemGroup>
            </Project>
            """);
        return path;
    }

    /// <summary>Writes a classic-format .sln referencing the given csprojs; returns its path.</summary>
    public string CreateSolution(string relativePath, params string[] csprojPaths)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var solutionDir = Path.GetDirectoryName(path)!;
        var entries = csprojPaths.Select((csproj, i) =>
            $"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = " +
            $"\"{Path.GetFileNameWithoutExtension(csproj)}\", " +
            $"\"{Path.GetRelativePath(solutionDir, csproj).Replace('/', '\\')}\", " +
            $"\"{{{Guid.NewGuid().ToString().ToUpperInvariant()}}}\"\nEndProject");
        File.WriteAllText(path,
            "Microsoft Visual Studio Solution File, Format Version 12.00\n" + string.Join("\n", entries) + "\n");
        return path;
    }

    /// <summary>A minimal catalog entry whose latest pin is <paramref name="latestCommit"/>.</summary>
    public static IndexedAsset CatalogEntry(string id, string name, string latestCommit,
        string latestRef = "master", string? certifiedTag = null, string? certifiedCommit = null) => new()
    {
        Id = id,
        Repo = $"https://github.com/test/{name}",
        Manifest = new AssetManifest
        {
            Id = id,
            Name = name,
            Description = "Synthetic test asset.",
            Category = "Scripts",
            License = "MIT",
        },
        Latest = new IndexedVersion { Ref = latestRef, Commit = latestCommit, ContentHash = "hash" },
        Certified = certifiedTag is null
            ? []
            : [new IndexedCertifiedVersion { Version = certifiedTag.TrimStart('v'), Tag = certifiedTag, Commit = certifiedCommit ?? latestCommit }],
        ValidationStatus = "ok",
    };

    public static Dictionary<string, IndexedAsset> Catalog(params IndexedAsset[] assets) =>
        assets.ToDictionary(a => a.Id, StringComparer.Ordinal);

    private static string Git(string workingDir, params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in args)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
        return stdout;
    }

    public void Dispose()
    {
        DesktopInstaller.AppDataOverride = null;
        try
        {
            // Force-clear read-only .git files before deleting.
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // Temp cleanup is best-effort.
        }
    }
}
