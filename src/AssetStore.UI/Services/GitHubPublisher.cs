// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AssetStore.Core.Models;
using AssetStore.Core.Serialization;

namespace AssetStore.App.Services;

/// <summary>Outcome of a publish attempt.</summary>
/// <param name="Success">Whether the PR was opened.</param>
/// <param name="PullRequestUrl">The PR URL on success.</param>
/// <param name="Error">A human-readable error on failure.</param>
public sealed record PublishResult(bool Success, string? PullRequestUrl, string? Error);

/// <summary>
/// Submits a registry entry to the AssetContainer repository via the GitHub REST API:
/// fork (if needed) → branch → commit registry/&lt;id&gt;.json → open a pull request.
/// Runs entirely from the browser against api.github.com (CORS-enabled with a token).
/// </summary>
public sealed class GitHubPublisher(HttpClient gitHub, GitHubAuth auth, RegistryOptions registry)
{
    private readonly string _registryOwner = registry.Owner;
    private readonly string _registryRepo = registry.Repo;
    private readonly string _baseBranch = registry.BaseBranch;

    /// <summary>Opens a PR that adds (or updates) <c>registry/&lt;id&gt;.json</c>.</summary>
    public async Task<PublishResult> PublishAsync(RegistryEntry entry, CancellationToken ct = default)
    {
        if (!auth.IsSignedIn)
        {
            return new PublishResult(false, null, "Not signed in.");
        }

        try
        {
            var ctx = await PrepareBranchAsync($"add-{Sanitize(entry.Id)}", ct);
            var path = $"registry/{entry.Id}.json";
            var existingSha = await GetFileShaAsync(ctx.HeadOwner, _registryRepo, path, ctx.Branch, ct);
            var content = Convert.ToBase64String(Encoding.UTF8.GetBytes(AssetStoreJson.Serialize(entry) + "\n"));
            await Put($"repos/{ctx.HeadOwner}/{_registryRepo}/contents/{path}", new
            {
                message = $"Add asset {entry.Id}",
                content,
                branch = ctx.Branch,
                sha = existingSha,
            }, ct);

            return await OpenPullRequestAsync(ctx, $"Add asset {entry.Id}",
                $"Submitting `{entry.Id}` from {entry.Repo} (ref `{entry.Latest.Ref}`).\n\n_Opened via the Stride Asset Store manage tool._", ct);
        }
        catch (PublishException ex)
        {
            return new PublishResult(false, null, ex.Message);
        }
        catch (Exception ex)
        {
            return new PublishResult(false, null, ex.Message);
        }
    }

    /// <summary>Opens a PR that adds a certified version to an existing <c>registry/&lt;id&gt;.json</c>.</summary>
    public async Task<PublishResult> CertifyAsync(string id, CertifiedVersion version, CancellationToken ct = default)
    {
        if (!auth.IsSignedIn)
        {
            return new PublishResult(false, null, "Not signed in.");
        }

        try
        {
            var ctx = await PrepareBranchAsync($"certify-{Sanitize(id)}", ct);
            var path = $"registry/{id}.json";
            var (entry, sha) = await GetEntryAsync(ctx.HeadOwner, path, ctx.Branch, ct);
            if (entry is null || sha is null)
            {
                return new PublishResult(false, null, $"registry/{id}.json was not found — is the asset published?");
            }

            var certified = entry.Certified.ToList();
            if (certified.Any(c => string.Equals(c.Commit, version.Commit, StringComparison.OrdinalIgnoreCase)))
            {
                return new PublishResult(false, null, $"Commit {version.Commit} is already certified for {id}.");
            }

            certified.Add(version);
            var content = Convert.ToBase64String(Encoding.UTF8.GetBytes(AssetStoreJson.Serialize(entry with { Certified = certified }) + "\n"));
            await Put($"repos/{ctx.HeadOwner}/{_registryRepo}/contents/{path}", new
            {
                message = $"Certify {id} {version.Version}",
                content,
                branch = ctx.Branch,
                sha,
            }, ct);

            return await OpenPullRequestAsync(ctx, $"Certify {id} {version.Version}",
                $"Certifying `{id}` version `{version.Version}` at commit `{version.Commit}`.\n\n_Opened via the Stride Asset Store manage tool._", ct);
        }
        catch (PublishException ex)
        {
            return new PublishResult(false, null, ex.Message);
        }
        catch (Exception ex)
        {
            return new PublishResult(false, null, ex.Message);
        }
    }

    /// <summary>Opens a PR that deletes <c>registry/&lt;id&gt;.json</c> (asset removal request).</summary>
    public async Task<PublishResult> RemoveAsync(string id, CancellationToken ct = default)
    {
        if (!auth.IsSignedIn)
        {
            return new PublishResult(false, null, "Not signed in.");
        }

        try
        {
            var ctx = await PrepareBranchAsync($"remove-{Sanitize(id)}", ct);
            var path = $"registry/{id}.json";
            var sha = await GetFileShaAsync(ctx.HeadOwner, _registryRepo, path, ctx.Branch, ct);
            if (sha is null)
            {
                return new PublishResult(false, null, $"registry/{id}.json was not found.");
            }

            await Send(HttpMethod.Delete, $"repos/{ctx.HeadOwner}/{_registryRepo}/contents/{path}", new
            {
                message = $"Remove asset {id}",
                branch = ctx.Branch,
                sha,
            }, ct);

            return await OpenPullRequestAsync(ctx, $"Remove asset {id}",
                $"Requesting removal of `{id}` from the registry.\n\n_Opened via the Stride Asset Store manage tool._", ct);
        }
        catch (PublishException ex)
        {
            return new PublishResult(false, null, ex.Message);
        }
        catch (Exception ex)
        {
            return new PublishResult(false, null, ex.Message);
        }
    }

    private sealed record BranchContext(string Login, string HeadOwner, string Branch, bool OnUpstream);

    /// <summary>Forks the registry (unless owned) and creates a fresh working branch off the base branch.</summary>
    private async Task<BranchContext> PrepareBranchAsync(string branchPrefix, CancellationToken ct)
    {
        var login = auth.Login!;
        var onUpstream = string.Equals(login, _registryOwner, StringComparison.OrdinalIgnoreCase);

        if (!onUpstream)
        {
            await Post($"repos/{_registryOwner}/{_registryRepo}/forks", new { }, ct);
            await WaitForForkAsync(login, ct);
        }

        // Branch off the UPSTREAM base commit (a fork shares the object store) so reads and the PR are
        // against the CURRENT registry, not a possibly-stale fork that was created long ago.
        var baseSha = await GetBranchShaAsync(_registryOwner, _registryRepo, _baseBranch, ct)
            ?? throw new InvalidOperationException($"Could not read '{_baseBranch}' of {_registryOwner}/{_registryRepo}.");

        var branch = $"{branchPrefix}-{Guid.NewGuid():N}";
        await Post($"repos/{login}/{_registryRepo}/git/refs",
            new { @ref = $"refs/heads/{branch}", sha = baseSha }, ct);

        return new BranchContext(login, login, branch, onUpstream);
    }

    private async Task<PublishResult> OpenPullRequestAsync(BranchContext ctx, string title, string body, CancellationToken ct)
    {
        var head = ctx.OnUpstream ? ctx.Branch : $"{ctx.Login}:{ctx.Branch}";
        using var pr = await Post($"repos/{_registryOwner}/{_registryRepo}/pulls", new
        {
            title,
            head,
            @base = _baseBranch,
            body,
        }, ct);

        var url = pr.RootElement.GetProperty("html_url").GetString();
        return new PublishResult(true, url, null);
    }

    private async Task WaitForForkAsync(string owner, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var request = Build(HttpMethod.Get, $"repos/{owner}/{_registryRepo}");
            using var response = await gitHub.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            await Task.Delay(1500, ct);
        }

        throw new PublishException("The fork did not become available in time. Please try again.");
    }

    private async Task<string?> GetBranchShaAsync(string owner, string repo, string branch, CancellationToken ct)
    {
        using var request = Build(HttpMethod.Get, $"repos/{owner}/{repo}/git/ref/heads/{branch}");
        using var response = await gitHub.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("object").GetProperty("sha").GetString();
    }

    private async Task<string?> GetFileShaAsync(string owner, string repo, string path, string branch, CancellationToken ct)
    {
        using var request = Build(HttpMethod.Get, $"repos/{owner}/{repo}/contents/{path}?ref={branch}");
        using var response = await gitHub.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            // A transient 403/500 must not be reported to the user as "file not found".
            throw new PublishException(Describe(response.StatusCode, await response.Content.ReadAsStringAsync(ct)));
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("sha", out var sha) ? sha.GetString() : null;
    }

    /// <summary>Reads and deserializes a registry entry (with its blob sha for a subsequent update).</summary>
    private async Task<(RegistryEntry? Entry, string? Sha)> GetEntryAsync(string owner, string path, string branch, CancellationToken ct)
    {
        using var request = Build(HttpMethod.Get, $"repos/{owner}/{_registryRepo}/contents/{path}?ref={branch}");
        using var response = await gitHub.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            return (null, null);
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("content", out var contentElement))
        {
            return (null, null);
        }

        var sha = doc.RootElement.TryGetProperty("sha", out var shaElement) ? shaElement.GetString() : null;
        var base64 = (contentElement.GetString() ?? "").Replace("\n", "");
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        return (AssetStoreJson.Deserialize<RegistryEntry>(json), sha);
    }

    private Task<JsonDocument> Post(string url, object body, CancellationToken ct) => Send(HttpMethod.Post, url, body, ct);

    private Task<JsonDocument> Put(string url, object body, CancellationToken ct) => Send(HttpMethod.Put, url, body, ct);

    private async Task<JsonDocument> Send(HttpMethod method, string url, object body, CancellationToken ct)
    {
        using var request = Build(method, url);
        request.Content = JsonContent.Create(body);
        using var response = await gitHub.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new PublishException(Describe(response.StatusCode, text));
        }

        return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
    }

    private HttpRequestMessage Build(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    private static string Describe(HttpStatusCode status, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var message))
            {
                return $"GitHub {(int)status}: {message.GetString()}";
            }
        }
        catch
        {
            // fall through
        }

        return $"GitHub {(int)status}.";
    }

    private static string Sanitize(string id) =>
        new(id.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());

    private sealed class PublishException(string message) : Exception(message);
}
