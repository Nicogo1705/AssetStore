// Copyright (c) 2026 Nicogo
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace AssetStore.App.Services;

/// <summary>Renders untrusted third-party Markdown (asset READMEs) to safe HTML.</summary>
/// <remarks>
/// <c>DisableHtml()</c> strips raw HTML, but Markdig does not filter link destinations — a
/// <c>[click me](javascript:…)</c> in a README would stay clickable. So after parsing, every
/// link/image URL is checked against a scheme whitelist (http/https/mailto, plus relative paths),
/// and external links get <c>target="_blank" rel="noopener"</c>.
/// </remarks>
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().DisableHtml().UseAdvancedExtensions().Build();

    public static string ToSafeHtml(string markdown)
    {
        var document = Markdown.Parse(markdown, Pipeline);

        foreach (var link in document.Descendants<LinkInline>())
        {
            if (!IsSafeUrl(link.Url, allowMailto: !link.IsImage))
            {
                link.Url = "#"; // neutralized; the link text still renders
            }
            else if (IsExternal(link.Url) && !link.IsImage)
            {
                var attributes = link.GetAttributes();
                attributes.AddProperty("target", "_blank");
                attributes.AddProperty("rel", "noopener");
            }
        }

        // Autolinks (<scheme:…>) accept any scheme per CommonMark; render unsafe ones as plain text.
        foreach (var autolink in document.Descendants<AutolinkInline>().ToList())
        {
            if (!IsSafeUrl(autolink.Url, allowMailto: true))
            {
                autolink.ReplaceBy(new LiteralInline(autolink.Url));
            }
            else if (IsExternal(autolink.Url))
            {
                var attributes = autolink.GetAttributes();
                attributes.AddProperty("target", "_blank");
                attributes.AddProperty("rel", "noopener");
            }
        }

        var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        renderer.Render(document);
        writer.Flush();
        return writer.ToString();
    }

    private static bool IsSafeUrl(string? url, bool allowMailto)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return true; // relative path or anchor — resolves within the site
        }

        return uri.Scheme is "http" or "https" || (allowMailto && uri.Scheme == "mailto");
    }

    private static bool IsExternal(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
}
