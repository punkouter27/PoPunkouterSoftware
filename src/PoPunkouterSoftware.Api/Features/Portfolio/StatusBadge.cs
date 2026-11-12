using System.Security;

namespace PoPunkouterSoftware.Api;

/// <summary>
/// A shields.io-style flat badge, drawn by hand: two rects and two text runs are not worth a
/// dependency or a round trip to shields.io. Widths are estimated at ~6.6px per character of
/// 11px Verdana, which is what shields itself assumes for its fallback metrics.
/// </summary>
internal static class StatusBadge
{
    internal static string Render(string label, string message, string color)
    {
        static int Width(string s) => (int)Math.Ceiling(s.Length * 6.6) + 12;

        var lw = Width(label);
        var mw = Width(message);
        var w = lw + mw;
        // Escaped even though both strings come from the curated catalog and a fixed set of
        // messages: this is XML served from the site's own origin.
        var l = SecurityElement.Escape(label);
        var m = SecurityElement.Escape(message);

        return $"""
            <svg xmlns="http://www.w3.org/2000/svg" width="{w}" height="20" role="img" aria-label="{l}: {m}">
              <title>{l}: {m}</title>
              <clipPath id="r"><rect width="{w}" height="20" rx="3"/></clipPath>
              <g clip-path="url(#r)">
                <rect width="{lw}" height="20" fill="#555"/>
                <rect x="{lw}" width="{mw}" height="20" fill="{color}"/>
              </g>
              <g fill="#fff" text-anchor="middle" font-family="Verdana,DejaVu Sans,sans-serif" font-size="11">
                <text x="{lw / 2}" y="14">{l}</text>
                <text x="{lw + mw / 2}" y="14">{m}</text>
              </g>
            </svg>
            """;
    }
}
