// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AssetStore.App.Services;
using AssetStore.Core.Models;
using AssetStore.Core.Serialization;

namespace AssetStore.Desktop.Services;

/// <summary>
/// Desktop implementation of <see cref="ICliPublisher"/>: opens registry pull requests through the
/// GitHub CLI (<c>gh api</c>), authenticated with the user's existing <c>gh auth login</c> — so no
/// token is ever entered into the app. It mirrors the REST flow of the browser publisher
/// (fork → branch → write registry/&lt;id&gt;.json → open a pull request), only via the local CLI.
/// </summary>
public sealed class GhCliPublisher(RegistryOptions registry) : ICliPublisher
{
    private readonly string _owner = registry.Owner;
    private readonly string _repo = registry.Repo;
    private readonly string _base = registry.BaseBranch;

    public bool Supported => true;

    public async Task<CliStatus> CheckAsync(CancellationToken ct = default)
    {
        var git = await RunAsync("git", ["--version"], ct);
        var gh = await RunAsync("gh", ["--version"], ct);
        var authed = false;
        string? account = null;
        if (gh.Ok)
        {
            var status = await RunAsync("gh", ["auth", "status"], ct);
            authed = status.Ok;
            if (authed)
            {
                var login = await RunAsync("gh", ["api", "user", "-q", ".login"], ct);
                account = login.Ok ? login.StdOut.Trim() : null;
            }
        }

        return new CliStatus(
            git.Ok, FirstLine(git.StdOut),
            gh.Ok, FirstLine(gh.StdOut),
            authed, account);
    }

    public Task<PublishResult> PublishAsync(RegistryEntry entry, CancellationToken ct = default) =>
        RunFlowAsync($"add-{Sanitize(entry.Id)}", $"Add asset {entry.Id}",
            $"Submitting `{entry.Id}` from {entry.Repo} (ref `{entry.Latest.Ref}`).\n\n_Opened via the Stride Asset Store manage tool (CLI)._",
            async (ctx) =>
            {
                var path = $"registry/{entry.Id}.json";
                var existing = await GetFileShaAsync(ctx.HeadOwner, path, ctx.Branch, ct);
                await PutFileAsync(ctx.HeadOwner, path, ctx.Branch, AssetStoreJson.Serialize(entry) + "\n",
                    $"Add asset {entry.Id}", existing, ct);
                return null;
            }, ct);

    public Task<PublishResult> CertifyAsync(string id, CertifiedVersion version, CancellationToken ct = default) =>
        RunFlowAsync($"certify-{Sanitize(id)}", $"Certify {id} {version.Version}",
            $"Certifying `{id}` version `{version.Version}` at commit `{version.Commit}`.\n\n_Opened via the Stride Asset Store manage tool (CLI)._",
            async (ctx) =>
            {
                var path = $"registry/{id}.json";
                var (entry, sha) = await GetEntryAsync(ctx.HeadOwner, path, ctx.Branch, ct);
                if (entry is null || sha is null)
                {
                    return $"registry/{id}.json was not found — is the asset published?";
                }

                var certified = entry.Certified.ToList();
                if (certified.Any(c => string.Equals(c.Commit, version.Commit, StringComparison.OrdinalIgnoreCase)))
                {
                    return $"Commit {version.Commit} is already certified for {id}.";
                }

                certified.Add(version);
                await PutFileAsync(ctx.HeadOwner, path, ctx.Branch,
                    AssetStoreJson.Serialize(entry with { Certified = certified }) + "\n",
                    $"Certify {id} {version.Version}", sha, ct);
                return null;
            }, ct);

    public Task<PublishResult> RemoveAsync(string id, CancellationToken ct = default) =>
        RunFlowAsync($"remove-{Sanitize(id)}", $"Remove asset {id}",
            $"Requesting removal of `{id}` from the registry.\n\n_Opened via the Stride Asset Store manage tool (CLI)._",
            async (ctx) =>
            {
                var path = $"registry/{id}.json";
                var sha = await GetFileShaAsync(ctx.HeadOwner, path, ctx.Branch, ct);
                if (sha is null)
                {
                    return $"registry/{id}.json was not found.";
                }

                var del = await RunAsync("gh",
                    ["api", $"repos/{ctx.HeadOwner}/{_repo}/contents/{path}", "-X", "DELETE",
                     "-f", $"message=Remove asset {id}", "-f", $"branch={ctx.Branch}", "-f", $"sha={sha}"], ct);
                return del.Ok ? null : Describe(del);
            }, ct);

    // ── Flow scaffolding ─────────────────────────────────────────────────────

    private sealed record Ctx(string Login, string HeadOwner, string Branch, bool OnUpstream);

    /// <summary>Runs the shared fork → branch → (write) → PR flow. <paramref name="write"/> returns an error
    /// message to abort, or null to continue to opening the PR.</summary>
    private async Task<PublishResult> RunFlowAsync(
        string branchPrefix, string title, string body,
        Func<Ctx, Task<string?>> write, CancellationToken ct)
    {
        try
        {
            var login = await GhStringAsync(["api", "user", "-q", ".login"], ct);
            if (login is null)
            {
                return new PublishResult(false, null, "gh is not authenticated. Run `gh auth login` (see the Prerequisites page).");
            }

            var onUpstream = string.Equals(login, _owner, StringComparison.OrdinalIgnoreCase);
            if (!onUpstream)
            {
                await RunAsync("gh", ["api", $"repos/{_owner}/{_repo}/forks", "-X", "POST"], ct);
                if (!await WaitForForkAsync(login, ct))
                {
                    return new PublishResult(false, null, "The fork did not become available in time. Please try again.");
                }
            }

            var baseSha = await GhStringAsync(["api", $"repos/{_owner}/{_repo}/git/ref/heads/{_base}", "-q", ".object.sha"], ct);
            if (baseSha is null)
            {
                return new PublishResult(false, null, $"Could not read '{_base}' of {_owner}/{_repo}.");
            }

            var branch = $"{branchPrefix}-{Guid.NewGuid():N}";
            var mkRef = await RunAsync("gh",
                ["api", $"repos/{login}/{_repo}/git/refs", "-X", "POST",
                 "-f", $"ref=refs/heads/{branch}", "-f", $"sha={baseSha}"], ct);
            if (!mkRef.Ok)
            {
                return new PublishResult(false, null, Describe(mkRef));
            }

            var ctx = new Ctx(login, login, branch, onUpstream);
            var writeError = await write(ctx);
            if (writeError is not null)
            {
                return new PublishResult(false, null, writeError);
            }

            var head = onUpstream ? branch : $"{login}:{branch}";
            var url = await GhStringAsync(
                ["api", $"repos/{_owner}/{_repo}/pulls", "-X", "POST",
                 "-f", $"title={title}", "-f", $"head={head}", "-f", $"base={_base}", "-f", $"body={body}",
                 "-q", ".html_url"], ct);

            return url is not null
                ? new PublishResult(true, url, null)
                : new PublishResult(false, null, "The pull request could not be opened.");
        }
        catch (Exception ex)
        {
            return new PublishResult(false, null, ex.Message);
        }
    }

    private async Task<bool> WaitForForkAsync(string login, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if ((await RunAsync("gh", ["api", $"repos/{login}/{_repo}"], ct)).Ok)
            {
                return true;
            }

            await Task.Delay(1500, ct);
        }

        return false;
    }

    private async Task PutFileAsync(string owner, string path, string branch, string content, string message, string? sha, CancellationToken ct)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
        var args = new List<string>
        {
            "api", $"repos/{owner}/{_repo}/contents/{path}", "-X", "PUT",
            "-f", $"message={message}", "-f", $"content={b64}", "-f", $"branch={branch}",
        };
        if (sha is not null)
        {
            args.Add("-f");
            args.Add($"sha={sha}");
        }

        var res = await RunAsync("gh", args, ct);
        if (!res.Ok)
        {
            throw new InvalidOperationException(Describe(res));
        }
    }

    private async Task<string?> GetFileShaAsync(string owner, string path, string branch, CancellationToken ct)
    {
        var res = await RunAsync("gh", ["api", $"repos/{owner}/{_repo}/contents/{path}?ref={branch}", "-q", ".sha"], ct);
        return res.Ok ? res.StdOut.Trim() : null; // not found → non-zero exit → null
    }

    private async Task<(RegistryEntry? Entry, string? Sha)> GetEntryAsync(string owner, string path, string branch, CancellationToken ct)
    {
        var res = await RunAsync("gh", ["api", $"repos/{owner}/{_repo}/contents/{path}?ref={branch}"], ct);
        if (!res.Ok)
        {
            return (null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(res.StdOut);
            var sha = doc.RootElement.TryGetProperty("sha", out var s) ? s.GetString() : null;
            var b64 = (doc.RootElement.TryGetProperty("content", out var c) ? c.GetString() : "")?.Replace("\n", "") ?? "";
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            return (AssetStoreJson.Deserialize<RegistryEntry>(json), sha);
        }
        catch
        {
            return (null, null);
        }
    }

    // ── Process helpers ──────────────────────────────────────────────────────

    private sealed record ProcResult(int ExitCode, string StdOut, string StdErr)
    {
        public bool Ok => ExitCode == 0;
    }

    private async Task<string?> GhStringAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var r = await RunAsync("gh", args, ct);
        return r.Ok && !string.IsNullOrWhiteSpace(r.StdOut) ? r.StdOut.Trim() : null;
    }

    private static Task<ProcResult> RunAsync(string exe, IReadOnlyList<string> args, CancellationToken ct) =>
        Task.Run(() =>
        {
            var info = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args)
            {
                info.ArgumentList.Add(a);
            }

            try
            {
                using var p = Process.Start(info);
                if (p is null)
                {
                    return new ProcResult(-1, "", $"Unable to start '{exe}'.");
                }

                var stdout = p.StandardOutput.ReadToEndAsync(ct);
                var stderr = p.StandardError.ReadToEndAsync(ct);
                p.WaitForExit();
                return new ProcResult(p.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
            }
            catch (Exception ex)
            {
                // Executable not found on PATH, etc.
                return new ProcResult(-1, "", ex.Message);
            }
        }, ct);

    private static string Describe(ProcResult r)
    {
        var err = r.StdErr.Trim();
        if (string.IsNullOrEmpty(err))
        {
            err = r.StdOut.Trim();
        }

        return string.IsNullOrEmpty(err) ? $"gh exited with code {r.ExitCode}." : err;
    }

    private static string? FirstLine(string s)
    {
        var line = s.Split('\n', 2)[0].Trim();
        return string.IsNullOrEmpty(line) ? null : line;
    }

    private static string Sanitize(string id) =>
        new(id.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
}
