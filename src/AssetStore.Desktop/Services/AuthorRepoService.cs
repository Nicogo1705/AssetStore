// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Diagnostics;
using System.Text.Json;
using AssetStore.Core.Models;

namespace AssetStore.Desktop.Services;

/// <summary>A tracked local asset repository (the author's working copy) and its git state.</summary>
public sealed record AuthorRepo(
    string Root,
    string? Id,              // manifest id (null when AssetData/manifest.json is missing/unreadable)
    string? Name,
    string Branch,           // current branch ("?" outside a repo)
    int Dirty,               // uncommitted changes (files)
    int Ahead,               // commits not pushed to upstream (0 when no upstream)
    int Behind,              // upstream commits not pulled
    bool HasUpstream,
    string? LatestTag,       // newest v* tag (by version), null when untagged
    string HeadCommit,
    string? RemoteUrl,       // origin URL, normalized to https (null when no remote)
    IReadOnlyList<string> Tags); // all v* tags, newest first

/// <summary>
/// The "My assets" authoring manager: tracks the user's own asset repos (local git working
/// copies), reads their git state, and runs the publishing actions — commit &amp; push, tag
/// push — locally via git. The tracked list persists in
/// <c>%APPDATA%/StrideAssetStore/author-repos.json</c>, like <see cref="ProjectStore"/>.
/// </summary>
public sealed class AuthorRepoService
{
    private readonly string _file;
    private readonly Lock _gate = new();

    public AuthorRepoService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StrideAssetStore");
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "author-repos.json");
    }

    /// <summary>The tracked repo folders, most-recently-added last.</summary>
    public IReadOnlyList<string> List()
    {
        lock (_gate)
        {
            return Read();
        }
    }

    /// <summary>Tracks a folder (idempotent). Returns null on success, else why it was rejected.</summary>
    public string? Add(string path)
    {
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(Path.Combine(full, ".git")))
        {
            return "That folder isn't a git repository (no .git).";
        }

        if (!File.Exists(Path.Combine(full, "AssetData", "manifest.json")))
        {
            return "That folder isn't an asset repo (no AssetData/manifest.json).";
        }

        lock (_gate)
        {
            var list = Read();
            if (!list.Any(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(full);
                Write(list);
            }
        }

        return null;
    }

    /// <summary>Stops tracking a folder (never touches the repo on disk).</summary>
    public void Remove(string path)
    {
        var full = Path.GetFullPath(path);
        lock (_gate)
        {
            var list = Read();
            if (list.RemoveAll(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                Write(list);
            }
        }
    }

    /// <summary>Reads a tracked repo's current manifest + git state (branch, dirty, ahead/behind, tags).</summary>
    public AuthorRepo Inspect(string root)
    {
        string? id = null, name = null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "AssetData", "manifest.json")));
            id = doc.RootElement.TryGetProperty("id", out var i) ? i.GetString() : null;
            name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
        }
        catch
        {
            // Broken manifest: still list the repo so the user can fix it from here.
        }

        var branch = Git(root, "rev-parse", "--abbrev-ref", "HEAD").Stdout.Trim() is { Length: > 0 } b ? b : "?";
        var dirty = Git(root, "status", "--porcelain").Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        var head = Git(root, "rev-parse", "HEAD").Stdout.Trim();

        var (ahead, behind, hasUpstream) = (0, 0, false);
        var counts = Git(root, "rev-list", "--left-right", "--count", "@{u}...HEAD");
        if (counts.ExitCode == 0
            && counts.Stdout.Trim().Split('\t', ' ') is [{ } behindStr, { } aheadStr]
            && int.TryParse(behindStr, out var parsedBehind) && int.TryParse(aheadStr, out var parsedAhead))
        {
            // Assigned only when BOTH parse — a partial parse must not leave a stray "behind".
            (ahead, behind, hasUpstream) = (parsedAhead, parsedBehind, true);
        }

        var tags = Git(root, "tag", "--list", "v*", "--sort=-v:refname").Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        var remote = Git(root, "remote", "get-url", "origin").Stdout.Trim();
        var remoteUrl = remote.Length == 0 ? null : NormalizeRemote(remote);

        return new AuthorRepo(root, id, name, branch, dirty, ahead, behind, hasUpstream,
            tags.FirstOrDefault(), head, remoteUrl, tags);
    }

    /// <summary>The commit a tag points at (annotated tags dereferenced), or null.</summary>
    public string? TagCommit(string root, string tag)
    {
        var (exit, stdout, _) = Git(root, "rev-list", "-n", "1", tag);
        return exit == 0 && stdout.Trim() is { Length: >= 7 } commit ? commit : null;
    }

    /// <summary>Stages everything, commits (when there is anything to commit) and pushes.</summary>
    public InstallResult CommitAndPush(string root, string message)
    {
        var messages = new List<string>();
        var add = Git(root, "add", "-A");
        if (add.ExitCode != 0)
        {
            return Fail(messages, $"git add failed: {add.Stderr.Trim()}");
        }

        var staged = Git(root, "status", "--porcelain").Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        if (staged > 0)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return Fail(messages, "Write a commit message.");
            }

            var commit = Git(root, "commit", "-q", "-m", message.Trim());
            if (commit.ExitCode != 0)
            {
                return Fail(messages, $"git commit failed: {commit.Stderr.Trim()}");
            }

            messages.Add($"✓ Committed {staged} change(s).");
        }
        else
        {
            messages.Add("• Nothing to commit — pushing what's already committed.");
        }

        // -u so the first push of a fresh branch just works.
        var push = Git(root, "push", "-q", "-u", "origin", "HEAD");
        if (push.ExitCode != 0)
        {
            return Fail(messages, $"git push failed: {push.Stderr.Trim()}");
        }

        messages.Add("✓ Pushed.");
        return new InstallResult(true, messages);
    }

    /// <summary>Creates an annotated tag on HEAD and pushes it (commits must be pushed first).</summary>
    public InstallResult PushTag(string root, string tag)
    {
        var messages = new List<string>();
        tag = tag.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+$"))
        {
            return Fail(messages, "Tags are vMAJOR.MINOR.PATCH, e.g. v1.2.0.");
        }

        var create = Git(root, "tag", "-a", tag, "-m", $"Release {tag}");
        if (create.ExitCode != 0)
        {
            return Fail(messages, $"git tag failed: {create.Stderr.Trim()}");
        }

        var push = Git(root, "push", "-q", "origin", tag);
        if (push.ExitCode != 0)
        {
            return Fail(messages, $"git push failed: {push.Stderr.Trim()} (the local tag {tag} was created — delete it with 'git tag -d {tag}' to retry)");
        }

        messages.Add($"✓ Tag {tag} pushed — the store's daily index refresh will pick it up as a release.");
        return new InstallResult(true, messages);
    }

    /// <summary>
    /// URL of an open submission PR for this asset on the registry, or null. Best-effort via the
    /// gh CLI (searches open PRs whose title mentions the asset id — the publisher titles them
    /// "Add asset &lt;id&gt;"); null when gh is missing/offline.
    /// </summary>
    public string? FindOpenPrUrl(string assetId, string registryOwner, string registryRepo)
    {
        try
        {
            var (exit, stdout, _) = Run("gh",
                ["pr", "list", "-R", $"{registryOwner}/{registryRepo}", "--state", "open",
                 "--search", $"\"{assetId}\" in:title", "--json", "url", "--jq", ".[0].url"]);
            var url = stdout.Trim();
            return exit == 0 && url.StartsWith("https://", StringComparison.Ordinal) ? url : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The next patch version after the latest v* tag (v1.0.0 when untagged) — the tag input's prefill.</summary>
    public static string SuggestNextTag(string? latestTag)
    {
        var version = StrideVersionish(latestTag);
        return version is null ? "v1.0.0" : $"v{version.Major}.{version.Minor}.{Math.Max(version.Build, 0) + 1}";
    }

    private static Version? StrideVersionish(string? tag) =>
        tag is not null && Version.TryParse(tag.TrimStart('v', 'V'), out var v) ? v : null;

    private static string NormalizeRemote(string remote)
    {
        var url = remote.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase)
            ? "https://github.com/" + remote["git@github.com:".Length..]
            : remote;
        url = url.TrimEnd('/');
        // Only a trailing .git suffix — a blanket Replace would mangle "my.github-tools".
        return url.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? url[..^4] : url;
    }

    private static InstallResult Fail(List<string> messages, string error)
    {
        messages.Add($"✗ {error}");
        return new InstallResult(false, messages);
    }

    private static (int ExitCode, string Stdout, string Stderr) Git(string workingDir, params string[] args) =>
        Run("git", args, workingDir);

    private static (int ExitCode, string Stdout, string Stderr) Run(string exe, IReadOnlyList<string> args, string? workingDir = null)
    {
        var info = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workingDir ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
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
                return (-1, "", $"{exe} not found");
            }

            // Both streams concurrently — sequential ReadToEnd deadlocks when the child fills
            // the stderr pipe while we're still draining stdout (verbose git push output).
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            return (-1, "", ex.Message);
        }
    }

    private List<string> Read()
    {
        try
        {
            return File.Exists(_file)
                ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_file)) ?? []
                : [];
        }
        catch
        {
            return [];
        }
    }

    private void Write(List<string> list)
    {
        try
        {
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _file, overwrite: true);
        }
        catch
        {
            // Best-effort, like ProjectStore.
        }
    }
}
