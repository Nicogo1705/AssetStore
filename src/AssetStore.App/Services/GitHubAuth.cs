// Copyright (c) Stride contributors (https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.JSInterop;

namespace AssetStore.App.Services;

/// <summary>
/// Holds a user-supplied GitHub token (fine-grained PAT) for publishing.
/// </summary>
/// <remarks>
/// Browser WASM cannot complete GitHub's OAuth/Device Flow (the token endpoints lack CORS), so the
/// web app uses a pasted token. The token is kept in localStorage and sent only to api.github.com.
/// The desktop build (Phase 6) can switch to Device Flow.
/// </remarks>
public sealed class GitHubAuth(IJSRuntime js, HttpClient gitHub)
{
    private const string StorageKey = "assetstore.ghtoken";

    public string? Token { get; private set; }

    public string? Login { get; private set; }

    public bool IsSignedIn => !string.IsNullOrEmpty(Token) && !string.IsNullOrEmpty(Login);

    /// <summary>The api.github.com client (shared with the publisher).</summary>
    public HttpClient Http => gitHub;

    public event Action? Changed;

    /// <summary>Restores a previously stored token and validates it.</summary>
    public async Task RestoreAsync()
    {
        var token = await GetStored();
        if (!string.IsNullOrEmpty(token))
        {
            await SignInAsync(token);
        }
    }

    /// <summary>Validates a token via GET /user; on success stores it and records the login.</summary>
    public async Task<bool> SignInAsync(string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "user");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await gitHub.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Token = token;
        Login = doc.RootElement.GetProperty("login").GetString();
        await js.InvokeVoidAsync("localStorage.setItem", StorageKey, token);
        Changed?.Invoke();
        return true;
    }

    public async Task SignOutAsync()
    {
        Token = null;
        Login = null;
        await js.InvokeVoidAsync("localStorage.removeItem", StorageKey);
        Changed?.Invoke();
    }

    private async Task<string?> GetStored()
    {
        try
        {
            return await js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
        }
        catch
        {
            return null;
        }
    }
}
