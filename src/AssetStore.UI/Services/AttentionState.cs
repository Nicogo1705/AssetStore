// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Net.Http.Json;

namespace AssetStore.App.Services;

/// <summary>
/// The nav "attention dots" state (outdated/broken assets across tracked projects and the
/// shared cache). Pages call <see cref="RefreshAsync"/> after actions that change it (update,
/// uninstall, download) so the dots never lag behind reality; the layout re-renders on
/// <see cref="Changed"/>. Desktop-only data — refresh no-ops when the endpoint is absent.
/// </summary>
public sealed class AttentionState(HttpClient http)
{
    public sealed record Status(int Projects, int Assets);

    public Status? Current { get; private set; }

    public event Action? Changed;

    public async Task RefreshAsync()
    {
        try
        {
            Current = await http.GetFromJsonAsync<Status>("api/attention");
            Changed?.Invoke();
        }
        catch
        {
            // Online storefront (no endpoint) or transient failure - keep the last known state.
        }
    }
}
