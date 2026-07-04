// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.ComponentModel;
using System.Net;
using System.Text;
using AssetStore.Core.Models;
using AssetStore.Core.Serialization;
using Spectre.Console;
using Spectre.Console.Cli;

namespace AssetStore.Cli.Commands;

/// <summary>
/// Generates one static HTML snapshot per asset (<c>a/&lt;id&gt;/index.html</c>) with Open Graph
/// meta tags, plus a <c>sitemap.xml</c>. Blazor WASM has no SSR, so a shared asset link shows no
/// preview on Discord/Twitter/etc.; these snapshots give every asset a shareable mini-card (and
/// crawlable content for SEO) and instantly redirect humans to the SPA detail page.
/// </summary>
internal sealed class GeneratePagesCommand : Command<GeneratePagesCommand.Settings>
{
    internal sealed class Settings : CommandSettings
    {
        [CommandOption("-i|--index <PATH>")]
        [Description("Path to index.lock.json.")]
        public string Index { get; init; } = "index.lock.json";

        [CommandOption("-o|--out <DIR>")]
        [Description("Site root to write into (pages go to <out>/a/<id>/index.html).")]
        public required string Output { get; init; }

        [CommandOption("-s|--site <URL>")]
        [Description("Public base URL of the deployed site (e.g. https://user.github.io/AssetStore).")]
        public required string Site { get; init; }

        [CommandOption("--app-index <PATH>")]
        [Description("The published SPA's index.html. When given, each a/<id>/ page IS the app "
            + "(OG meta injected) instead of a redirect — the address bar becomes the shareable URL.")]
        public string? AppIndex { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var index = AssetStoreJson.Deserialize<IndexLock>(File.ReadAllText(settings.Index));
        var site = settings.Site.TrimEnd('/');
        var appShell = settings.AppIndex is null ? null : File.ReadAllText(settings.AppIndex);
        var sitemap = new StringBuilder()
            .AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""")
            .AppendLine("""<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">""")
            .AppendLine($"  <url><loc>{WebUtility.HtmlEncode(site)}/</loc></url>");

        var count = 0;
        foreach (var asset in index.Assets)
        {
            if (asset.ValidationStatus == "unavailable")
            {
                continue; // no meaningful manifest to advertise
            }

            var dir = Path.Combine(settings.Output, "a", asset.Id);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "index.html"),
                appShell is null ? RenderRedirectPage(asset, site) : RenderAppPage(asset, site, appShell));
            sitemap.AppendLine($"  <url><loc>{WebUtility.HtmlEncode($"{site}/a/{Uri.EscapeDataString(asset.Id)}/")}</loc></url>");
            count++;
        }

        sitemap.AppendLine("</urlset>");
        File.WriteAllText(Path.Combine(settings.Output, "sitemap.xml"), sitemap.ToString());
        File.WriteAllText(Path.Combine(settings.Output, "feed.xml"), RenderAtomFeed(index, site));

        AnsiConsole.MarkupLineInterpolated($"[green]Wrote[/] {count} OG page(s) + sitemap.xml + feed.xml under {settings.Output}");
        return 0;
    }

    /// <summary>Atom feed of store events (asset added, version certified), newest first — an
    /// auto-fed #new-assets channel for anyone subscribing (RSS readers, Discord bots).</summary>
    private static string RenderAtomFeed(IndexLock index, string site)
    {
        var events = new List<(string Date, string Title, string Url, string Summary)>();
        foreach (var asset in index.Assets)
        {
            if (asset.ValidationStatus == "unavailable")
            {
                continue;
            }

            var url = $"{site}/a/{Uri.EscapeDataString(asset.Id)}/";
            if (asset.AddedAt is { } added)
            {
                events.Add((added, $"New asset: {asset.Manifest.Name}", url, asset.Manifest.Description));
            }

            foreach (var certified in asset.Certified)
            {
                if (certified.CertifiedAt is { } date)
                {
                    events.Add((date, $"Certified: {asset.Manifest.Name} v{certified.Version}", url,
                        $"Version {certified.Version} of {asset.Manifest.Name} was reviewed and certified."));
                }
            }
        }

        var feed = new StringBuilder()
            .AppendLine("""<?xml version="1.0" encoding="utf-8"?>""")
            .AppendLine("""<feed xmlns="http://www.w3.org/2005/Atom">""")
            .AppendLine("  <title>Community Stride Asset Store — new assets &amp; certifications</title>")
            .AppendLine($"  <link href=\"{WebUtility.HtmlEncode(site)}/\"/>")
            .AppendLine($"  <link rel=\"self\" href=\"{WebUtility.HtmlEncode(site)}/feed.xml\"/>")
            .AppendLine($"  <id>{WebUtility.HtmlEncode(site)}/feed.xml</id>")
            .AppendLine($"  <updated>{WebUtility.HtmlEncode(NormalizeDate(index.GeneratedAt))}</updated>");

        // Dates are ISO-8601, so ordinal descending == newest first.
        foreach (var (date, title, url, summary) in events.OrderByDescending(e => e.Date, StringComparer.Ordinal).Take(30))
        {
            feed.AppendLine("  <entry>")
                .AppendLine($"    <title>{WebUtility.HtmlEncode(title)}</title>")
                .AppendLine($"    <link href=\"{WebUtility.HtmlEncode(url)}\"/>")
                .AppendLine($"    <id>{WebUtility.HtmlEncode($"{url}#{Uri.EscapeDataString(title)}")}</id>")
                .AppendLine($"    <updated>{WebUtility.HtmlEncode(NormalizeDate(date))}</updated>")
                .AppendLine($"    <summary>{WebUtility.HtmlEncode(summary)}</summary>")
                .AppendLine("  </entry>");
        }

        return feed.AppendLine("</feed>").ToString();
    }

    /// <summary>Atom requires full RFC-3339 timestamps; certifiedAt is a bare date ("2026-07-02").</summary>
    private static string NormalizeDate(string date) =>
        date.Length == 10 ? $"{date}T00:00:00Z" : date;

    /// <summary>The a/&lt;id&gt;/ page as the actual SPA shell with per-asset OG meta injected:
    /// crawlers read the card, humans get the app already at the right URL.</summary>
    private static string RenderAppPage(IndexedAsset asset, string site, string appShell)
    {
        var name = WebUtility.HtmlEncode(asset.Manifest.Name);
        var page = System.Text.RegularExpressions.Regex.Replace(
            appShell, "<title>.*?</title>", $"<title>{name} — Community Stride Asset Store</title>");
        return page.Replace("</head>", OgBlock(asset, site) + "</head>");
    }

    private static string OgBlock(IndexedAsset asset, string site)
    {
        var m = asset.Manifest;
        var name = WebUtility.HtmlEncode(m.Name);
        var description = WebUtility.HtmlEncode(m.Description);
        var pageUrl = WebUtility.HtmlEncode($"{site}/a/{Uri.EscapeDataString(asset.Id)}/");
        var image = string.IsNullOrEmpty(m.Thumbnail)
            ? null
            : WebUtility.HtmlEncode(RawRepoFile(asset.Repo, asset.Latest.Commit, m.Thumbnail));
        var certified = asset.Certified.Count > 0 ? " · ✔ certified" : "";

        return $"""
            <meta name="description" content="{description}">
            <link rel="canonical" href="{pageUrl}">
            <meta property="og:type" content="website">
            <meta property="og:site_name" content="Community Stride Asset Store">
            <meta property="og:title" content="{name}{certified}">
            <meta property="og:description" content="{description}">
            <meta property="og:url" content="{pageUrl}">
            {(image is null ? "" : $"""<meta property="og:image" content="{image}">""")}
            <meta name="twitter:card" content="{(image is null ? "summary" : "summary_large_image")}">

            """;
    }

    private static string RenderRedirectPage(IndexedAsset asset, string site)
    {
        var m = asset.Manifest;
        var name = WebUtility.HtmlEncode(m.Name);
        var description = WebUtility.HtmlEncode(m.Description);
        var pageUrl = WebUtility.HtmlEncode($"{site}/a/{Uri.EscapeDataString(asset.Id)}/");
        var appUrl = WebUtility.HtmlEncode($"{site}/asset?id={Uri.EscapeDataString(asset.Id)}");
        var image = string.IsNullOrEmpty(m.Thumbnail)
            ? null
            : WebUtility.HtmlEncode(RawRepoFile(asset.Repo, asset.Latest.Commit, m.Thumbnail));
        var certified = asset.Certified.Count > 0 ? " · ✔ certified" : "";

        return $"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <title>{name} — Community Stride Asset Store</title>
            <meta name="description" content="{description}">
            <link rel="canonical" href="{pageUrl}">
            <meta property="og:type" content="website">
            <meta property="og:site_name" content="Community Stride Asset Store">
            <meta property="og:title" content="{name}{certified}">
            <meta property="og:description" content="{description}">
            <meta property="og:url" content="{pageUrl}">
            {(image is null ? "" : $"""<meta property="og:image" content="{image}">""")}
            <meta name="twitter:card" content="{(image is null ? "summary" : "summary_large_image")}">
            <meta http-equiv="refresh" content="0;url={appUrl}">
            <script>location.replace("{appUrl}");</script>
            </head>
            <body>
            <p>Redirecting to <a href="{appUrl}">{name}</a>…</p>
            </body>
            </html>
            """;
    }

    /// <summary>Raw file URL at the pinned commit (same convention as the storefront).</summary>
    private static string RawRepoFile(string repo, string commit, string path)
    {
        var raw = repo.TrimEnd('/').Replace("https://github.com/", "https://raw.githubusercontent.com/");
        return $"{raw}/{commit}/{path.TrimStart('/')}";
    }
}
