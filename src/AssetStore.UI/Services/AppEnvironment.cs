// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Microsoft.JSInterop;

namespace AssetStore.App.Services;

/// <summary>
/// Detects whether the app runs locally (localhost / desktop) or from an online deployment, which
/// determines whether real local install is offered versus copy-the-commands guidance.
/// </summary>
public sealed class AppEnvironment(IJSRuntime js)
{
    public bool Initialized { get; private set; }

    private string Hostname { get; set; } = "";

    /// <summary>True when served from localhost or a desktop host (real install can be offered).</summary>
    public bool IsLocal { get; private set; }

    /// <summary>Whether the app can perform a real local install (clone + reference). Proto: local only.</summary>
    public bool InstallAvailable => IsLocal;

    public async Task InitializeAsync()
    {
        if (Initialized)
        {
            return;
        }

        try
        {
            Hostname = await js.InvokeAsync<string>("assetStoreEnv.hostname");
        }
        catch
        {
            Hostname = "";
        }

        IsLocal = Hostname is "localhost" or "127.0.0.1" or "[::1]" or ""
            || Hostname.EndsWith(".local", StringComparison.OrdinalIgnoreCase);
        Initialized = true;
    }

    /// <summary>Copies text to the clipboard (best-effort).</summary>
    public async Task CopyAsync(string text)
    {
        try
        {
            await js.InvokeVoidAsync("assetStoreEnv.copy", text);
        }
        catch
        {
            // Clipboard may be unavailable (insecure context); ignore.
        }
    }
}
