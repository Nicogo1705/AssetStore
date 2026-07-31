// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using AssetStore.App.Services;
using AssetStore.Core.Catalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace AssetStore.App;

/// <summary>Registers the shared Asset Store UI services. Hosts must also register an <see cref="ICatalogSource"/>.</summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAssetStoreUi(
        this IServiceCollection services, RegistryOptions? registry = null, AppInfo? app = null)
    {
        services.AddSingleton(registry ?? new RegistryOptions());
        services.AddSingleton(app ?? new AppInfo());

        services.AddScoped<ICatalogCache>(sp => new LocalStorageCatalogCache(sp.GetRequiredService<IJSRuntime>()));
        services.AddScoped(sp =>
            new CatalogLoader(sp.GetRequiredService<ICatalogSource>(), sp.GetRequiredService<ICatalogCache>()));
        services.AddScoped<CatalogState>();
        services.AddScoped<AttentionState>();
        services.AddScoped<AppEnvironment>();

        // GitHub publishing (PAT-based; api.github.com is CORS-enabled with a token).
        services.AddScoped(sp =>
        {
            var http = new HttpClient { BaseAddress = new Uri("https://api.github.com/") };
            // GitHub's REST API rejects requests without a User-Agent (403). In the WASM host the
            // browser sets one automatically; the desktop's server-side HttpClient does not — so set
            // it explicitly here for both hosts.
            http.DefaultRequestHeaders.UserAgent.ParseAdd("StrideAssetStore");
            return new GitHubAuth(sp.GetRequiredService<IJSRuntime>(), http);
        });
        services.AddScoped(sp =>
        {
            var auth = sp.GetRequiredService<GitHubAuth>();
            return new GitHubPublisher(auth.Http, auth, sp.GetRequiredService<RegistryOptions>());
        });

        services.AddScoped(sp =>
            new UpdateService(sp.GetRequiredService<GitHubAuth>(), sp.GetRequiredService<AppInfo>()));

        // Command-line publishing: browser fallback (no local tools). The desktop host overrides this
        // registration with a gh-based implementation after calling AddAssetStoreUi.
        services.AddScoped<ICliPublisher, NullCliPublisher>();

        return services;
    }
}
