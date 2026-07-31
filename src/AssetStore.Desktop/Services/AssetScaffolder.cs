// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Diagnostics;
using System.Text.Json;
using AssetStore.App.Services;

namespace AssetStore.Desktop.Services;

public sealed record ScaffoldRequest(
    string RepoName,
    string DisplayName,
    string Id,
    string Category,
    string License,
    string Description,
    string Tags,        // comma-separated
    string ParentDir,
    bool Private);

public sealed record ScaffoldResult(
    bool Success,
    IReadOnlyList<string> Messages,
    string? RepoUrl,
    string? CloneDir,
    string? SlnPath);

/// <summary>
/// The "New asset" wizard: instantiates the store's GitHub asset template
/// (<see cref="RegistryOptions.TemplateRepo"/>) into the user's account with <c>gh</c>, clones
/// it, applies every rename step of PUBLISHING.md (project/sln/scene/manifest), fills the
/// manifest from the form, removes the template's own instruction files, and pushes. The
/// result is a ready-to-build asset repo one Publish tab away from the store.
/// </summary>
public sealed class AssetScaffolder(RegistryOptions registry)
{
    private const string TemplateName = "StrideAssetTemplate";

    public async Task<ScaffoldResult> CreateAsync(ScaffoldRequest request, CancellationToken ct = default)
    {
        var messages = new List<string>();
        try
        {
            if (!Directory.Exists(request.ParentDir))
            {
                return Fail(messages, $"Folder '{request.ParentDir}' does not exist.");
            }

            var cloneDir = Path.Combine(request.ParentDir, request.RepoName);
            if (Directory.Exists(cloneDir))
            {
                return Fail(messages, $"'{cloneDir}' already exists — pick another repository name or folder.");
            }

            var owner = (await RunAsync("gh", ["api", "user", "-q", ".login"], request.ParentDir, ct)).Ok(out var login)
                ? login.Trim()
                : null;
            if (string.IsNullOrEmpty(owner))
            {
                return Fail(messages, "The GitHub CLI isn't signed in (run: gh auth login).");
            }

            // A leftover repo with the same name makes gh fail with an opaque GraphQL error — check first.
            var existing = await RunAsync("gh", ["repo", "view", $"{owner}/{request.RepoName}", "--json", "name"],
                request.ParentDir, ct);
            if (existing.Ok(out _))
            {
                return Fail(messages,
                    $"github.com/{owner}/{request.RepoName} already exists — pick another repository name (or delete the old repo first).");
            }

            // 1 · Instantiate the template on GitHub and clone it.
            messages.Add($"Creating {owner}/{request.RepoName} from {registry.TemplateRepo}…");
            var create = await RunAsync("gh",
                ["repo", "create", $"{owner}/{request.RepoName}",
                 "--template", registry.TemplateRepo,
                 request.Private ? "--private" : "--public", "--clone"],
                request.ParentDir, ct);
            if (!create.Ok(out _))
            {
                return Fail(messages, $"gh repo create failed: {create.StdErr.Trim()}");
            }

            // Template generation is asynchronous on GitHub's side — the initial clone can be
            // empty. Pull until the template files are actually there.
            var slnTemplate = Path.Combine(cloneDir, $"{TemplateName}.sln");
            for (var attempt = 0; attempt < 8 && !File.Exists(slnTemplate); attempt++)
            {
                await Task.Delay(1500, ct);
                await RunAsync("git", ["pull", "--quiet"], cloneDir, ct);
            }
            if (!File.Exists(slnTemplate))
            {
                return Fail(messages, "The template content never arrived in the clone — check the repo on GitHub and retry.");
            }

            // 2 · Renames (PUBLISHING.md steps): every textual occurrence, then files/folders.
            messages.Add("Applying renames…");
            foreach (var file in Directory.EnumerateFiles(cloneDir, "*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is ".cs" or ".csproj" or ".sln" or ".sdscene" or ".sdpkg" or ".md")
                {
                    var text = await File.ReadAllTextAsync(file, ct);
                    if (text.Contains(TemplateName, StringComparison.Ordinal))
                    {
                        await File.WriteAllTextAsync(file, text.Replace(TemplateName, request.RepoName), ct);
                    }
                }
            }

            var assetDataOld = Path.Combine(cloneDir, "AssetData", TemplateName);
            var assetDataNew = Path.Combine(cloneDir, "AssetData", request.RepoName);
            Directory.Move(assetDataOld, assetDataNew);
            File.Move(Path.Combine(assetDataNew, $"{TemplateName}.csproj"),
                      Path.Combine(assetDataNew, $"{request.RepoName}.csproj"));
            var slnPath = Path.Combine(cloneDir, $"{request.RepoName}.sln");
            File.Move(slnTemplate, slnPath);

            // 3 · Manifest from the form (thumbnail/media keep the template placeholders).
            var tags = request.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => t.TrimStart('#').ToLowerInvariant()).Distinct().ToArray();
            var manifest = new
            {
                schemaVersion = 1,
                id = request.Id,
                name = request.DisplayName,
                authors = new[] { new { name = owner, url = $"https://github.com/{owner}" } },
                description = request.Description,
                category = request.Category,
                tags,
                license = request.License,
                thumbnail = "thumbnail.png",
                media = new[] { "media/screenshot.png" },
                dependencies = Array.Empty<string>(),
                defaultImport = "local",
            };
            await File.WriteAllTextAsync(Path.Combine(cloneDir, "AssetData", "manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }) + "\n", ct);

            // 4 · README title + badge deep link; drop the template's own instruction files.
            var readmePath = Path.Combine(cloneDir, "README.md");
            if (File.Exists(readmePath))
            {
                var readme = await File.ReadAllTextAsync(readmePath, ct);
                readme = readme
                    .Replace("# Your Asset Name", $"# {request.DisplayName}")
                    .Replace("com.yourname.your-asset", request.Id);
                await File.WriteAllTextAsync(readmePath, readme, ct);
            }
            foreach (var scaffoldFile in new[] { "PUBLISHING.md", "registry-entry.example.json", Path.Combine("media", "GUIDE.md") })
            {
                var path = Path.Combine(cloneDir, scaffoldFile);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            // 5 · Commit and push.
            messages.Add("Pushing…");
            await RunAsync("git", ["add", "-A"], cloneDir, ct);
            var commit = await RunAsync("git",
                ["commit", "-q", "-m", $"Scaffold {request.DisplayName} from the asset template"], cloneDir, ct);
            if (!commit.Ok(out _))
            {
                return Fail(messages, $"git commit failed: {commit.StdErr.Trim()}");
            }
            var push = await RunAsync("git", ["push", "-q"], cloneDir, ct);
            if (!push.Ok(out _))
            {
                return Fail(messages, $"git push failed: {push.StdErr.Trim()}");
            }

            messages.Add("Done — the repo is live and the solution builds from the template baseline.");
            return new ScaffoldResult(true, messages,
                $"https://github.com/{owner}/{request.RepoName}", cloneDir, slnPath);
        }
        catch (Exception ex)
        {
            return Fail(messages, ex.Message);
        }
    }

    private static ScaffoldResult Fail(List<string> messages, string error)
    {
        messages.Add($"✗ {error}");
        return new ScaffoldResult(false, messages, null, null, null);
    }

    private sealed record ProcResult(int ExitCode, string StdOut, string StdErr)
    {
        public bool Ok(out string stdout)
        {
            stdout = StdOut;
            return ExitCode == 0;
        }
    }

    private static Task<ProcResult> RunAsync(string exe, IReadOnlyList<string> args, string workingDir, CancellationToken ct) =>
        Task.Run(() =>
        {
            var info = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDir,
            };
            foreach (var argument in args)
            {
                info.ArgumentList.Add(argument);
            }

            try
            {
                using var process = Process.Start(info);
                if (process is null)
                {
                    return new ProcResult(-1, "", $"Unable to start '{exe}'.");
                }

                var stdout = process.StandardOutput.ReadToEndAsync(ct);
                var stderr = process.StandardError.ReadToEndAsync(ct);
                process.WaitForExit();
                return new ProcResult(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
            }
            catch (Exception ex)
            {
                return new ProcResult(-1, "", ex.Message);
            }
        }, ct);
}
