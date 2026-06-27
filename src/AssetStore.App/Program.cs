// Copyright (c) Stride contributors (https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using AssetStore.App;
using AssetStore.App.Services;
using AssetStore.Core.Catalog;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.JSInterop;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var baseAddress = new Uri(builder.HostEnvironment.BaseAddress);
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = baseAddress });

// Where to fetch the aggregated index. Defaults to the bundled copy; override in appsettings.json
// (e.g. a raw GitHub URL) once the AssetContainer repository is public.
var indexUrl = builder.Configuration["Catalog:IndexUrl"] ?? "data/index.lock.json";

builder.Services.AddScoped<ICatalogSource>(sp =>
    new HttpCatalogSource(sp.GetRequiredService<HttpClient>(), new Uri(baseAddress, indexUrl)));
builder.Services.AddScoped<ICatalogCache>(sp =>
    new LocalStorageCatalogCache(sp.GetRequiredService<IJSRuntime>()));
builder.Services.AddScoped(sp =>
    new CatalogLoader(sp.GetRequiredService<ICatalogSource>(), sp.GetRequiredService<ICatalogCache>()));
builder.Services.AddScoped<CatalogState>();
builder.Services.AddScoped<AppEnvironment>();

// GitHub publishing (PAT-based; api.github.com is CORS-enabled with a token).
builder.Services.AddScoped(sp =>
    new GitHubAuth(sp.GetRequiredService<IJSRuntime>(), new HttpClient { BaseAddress = new Uri("https://api.github.com/") }));
builder.Services.AddScoped(sp =>
{
    var auth = sp.GetRequiredService<GitHubAuth>();
    return new GitHubPublisher(auth.Http, auth);
});

await builder.Build().RunAsync();
