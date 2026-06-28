// Copyright (c) Stride contributors (https://stride3d.net)
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

    public async Task<PublishResult> PublishAsync(RegistryEntry entry, CancellationToken ct = default)
    {
        if (!auth.IsSignedIn)
        {
            return new PublishResult(false, null, "Not signed in.");
        }

        try
        {
            var login = auth.Login!;
            var onUpstream = string.Equals(login, _registryOwner, StringComparison.OrdinalIgnoreCase);
            var headOwner = login;

            // 1. Fork the registry repo unless the user owns it.
            if (!onUpstream)
            {
                await Post($"repos/{_registryOwner}/{_registryRepo}/forks", new { }, ct);
                await WaitForForkAsync(login, ct);
            }

            // 2. Resolve the base commit and create a working branch on the head repo.
            var baseSha = await GetBranchShaAsync(headOwner, _registryRepo, _baseBranch, ct)
                ?? throw new InvalidOperationException($"Could not read '{_baseBranch}' of {headOwner}/{_registryRepo}.");

            var branch = $"add-{Sanitize(entry.Id)}-{Guid.NewGuid():N}";
            await Post($"repos/{headOwner}/{_registryRepo}/git/refs",
                new { @ref = $"refs/heads/{branch}", sha = baseSha }, ct);

            // 3. Commit registry/<id>.json (create or update).
            var path = $"registry/{entry.Id}.json";
            var existingSha = await GetFileShaAsync(headOwner, _registryRepo, path, branch, ct);
            var content = Convert.ToBase64String(Encoding.UTF8.GetBytes(AssetStoreJson.Serialize(entry) + "\n"));
            await Put($"repos/{headOwner}/{_registryRepo}/contents/{path}", new
            {
                message = $"Add asset {entry.Id}",
                content,
                branch,
                sha = existingSha,
            }, ct);

            // 4. Open the pull request against the upstream registry.
            var head = onUpstream ? branch : $"{login}:{branch}";
            using var pr = await Post($"repos/{_registryOwner}/{_registryRepo}/pulls", new
            {
                title = $"Add asset {entry.Id}",
                head,
                @base = _baseBranch,
                body = $"Submitting `{entry.Id}` from {entry.Repo} (ref `{entry.Latest.Ref}`).\n\n_Opened via the Stride Asset Store publish tool._",
            }, ct);

            var url = pr.RootElement.GetProperty("html_url").GetString();
            return new PublishResult(true, url, null);
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
            return null;
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("sha", out var sha) ? sha.GetString() : null;
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
