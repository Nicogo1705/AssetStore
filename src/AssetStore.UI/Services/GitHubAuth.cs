// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
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
/// web app uses a pasted token. It is kept encrypted at rest (AES-GCM with a non-extractable WebCrypto
/// key in IndexedDB), in sessionStorage so it is wiped when the tab/browser closes, and sent only to
/// api.github.com. See <c>js/interop.js</c> (<c>assetStoreSecureToken</c>). The desktop build can
/// switch to Device Flow.
/// </remarks>
public sealed class GitHubAuth(IJSRuntime js, HttpClient gitHub)
{
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
        await js.InvokeVoidAsync("assetStoreSecureToken.save", token);
        Changed?.Invoke();
        return true;
    }

    public async Task SignOutAsync()
    {
        Token = null;
        Login = null;
        await js.InvokeVoidAsync("assetStoreSecureToken.clear");
        Changed?.Invoke();
    }

    private async Task<string?> GetStored()
    {
        try
        {
            return await js.InvokeAsync<string?>("assetStoreSecureToken.load");
        }
        catch
        {
            return null;
        }
    }
}
