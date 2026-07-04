// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using AssetStore.App;
using AssetStore.App.Services;
using AssetStore.Core.Catalog;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var baseAddress = new Uri(builder.HostEnvironment.BaseAddress);
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = baseAddress });

// Where to fetch the aggregated index. appsettings.json points this at the registry's raw URL; the
// "data/index.lock.json" fallback is only used if that key is missing (no copy is bundled by default).
var indexUrl = builder.Configuration["Catalog:IndexUrl"] ?? "data/index.lock.json";
builder.Services.AddScoped<ICatalogSource>(sp =>
    new HttpCatalogSource(sp.GetRequiredService<HttpClient>(), new Uri(baseAddress, indexUrl)));

builder.Services.AddAssetStoreUi(builder.Configuration.GetSection("Registry").Get<RegistryOptions>());

await builder.Build().RunAsync();
