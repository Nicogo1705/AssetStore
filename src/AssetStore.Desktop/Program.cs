// Copyright (c) Stride contributors (https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Diagnostics;
using System.Runtime.InteropServices;
using AssetStore.App;
using AssetStore.Core.Catalog;
using AssetStore.Desktop.Components;

const string Url = "http://localhost:5111";

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    EnvironmentName = Environments.Production, // desktop app: no dev-time static asset patching
});
builder.WebHost.UseUrls(Url);
builder.WebHost.UseStaticWebAssets(); // serve _framework + RCL assets in Production/dotnet run
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// Local catalog snapshot + a self-pointing HttpClient (for the publish form's catalog metadata).
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(Url + "/") });
builder.Services.AddScoped<ICatalogSource>(sp =>
    new FileCatalogSource(Path.Combine(sp.GetRequiredService<IWebHostEnvironment>().WebRootPath, "data", "index.lock.json")));
builder.Services.AddAssetStoreUi();
builder.Services.AddScoped<AssetStore.Desktop.Services.DesktopInstaller>();

var app = builder.Build();
app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<AssetStore.Desktop.Components.App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(ServiceCollectionExtensions).Assembly); // routable pages live in the RCL

app.Lifetime.ApplicationStarted.Register(() => OpenBrowser(Url));
app.Run();

static void OpenBrowser(string url)
{
    try
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Process.Start("xdg-open", url);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Process.Start("open", url);
        }
    }
    catch
    {
        // If the browser can't be launched, the user can open the URL manually.
    }
}
