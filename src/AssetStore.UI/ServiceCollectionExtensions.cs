// Copyright (c) Stride contributors (https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using AssetStore.App.Services;
using AssetStore.Core.Catalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace AssetStore.App;

/// <summary>Registers the shared Asset Store UI services. Hosts must also register an <see cref="ICatalogSource"/>.</summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAssetStoreUi(this IServiceCollection services)
    {
        services.AddScoped<ICatalogCache>(sp => new LocalStorageCatalogCache(sp.GetRequiredService<IJSRuntime>()));
        services.AddScoped(sp =>
            new CatalogLoader(sp.GetRequiredService<ICatalogSource>(), sp.GetRequiredService<ICatalogCache>()));
        services.AddScoped<CatalogState>();
        services.AddScoped<AppEnvironment>();

        // GitHub publishing (PAT-based; api.github.com is CORS-enabled with a token).
        services.AddScoped(sp =>
            new GitHubAuth(sp.GetRequiredService<IJSRuntime>(), new HttpClient { BaseAddress = new Uri("https://api.github.com/") }));
        services.AddScoped(sp =>
        {
            var auth = sp.GetRequiredService<GitHubAuth>();
            return new GitHubPublisher(auth.Http, auth);
        });

        return services;
    }
}
