using System.Net;
using System.Text;

namespace PoPunkouterSoftware.Client;

/// <summary>
/// Minimal Markdown → HTML for the UNITE rules document. Everything is HTML-encoded before
/// any markup is added, so a document can never inject an element or an attribute, and only
/// http(s) links become anchors.
/// </summary>
internal static class GlobalMarkdown
{
    public static string ToHtml(string markdown)
    {
        var html = new StringBuilder(markdown.Length * 2);
        var inCode = false;
        var inList = false;

        foreach (var raw in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.TrimEnd();

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                CloseList(html, ref inList);
                html.Append(inCode ? "</code></pre>" : "<pre class=\"global-code\"><code>");
                inCode = !inCode;
                continue;
            }

            if (inCode)
            {
                html.Append(Encode(line)).Append('\n');
                continue;
            }

            if (line.Length == 0)
            {
                CloseList(html, ref inList);
                continue;
            }

            if (line.TrimStart().StartsWith('#'))
            {
                CloseList(html, ref inList);
                var level = Math.Clamp(line.Length - line.TrimStart('#').Length, 1, 4);
                html.Append($"<h{level + 1}>").Append(Inline(line.TrimStart('#').Trim())).Append($"</h{level + 1}>");
                continue;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            {
                if (!inList)
                {
                    html.Append("<ul>");
                    inList = true;
                }

                html.Append("<li>").Append(Inline(line[2..])).Append("</li>");
                continue;
            }

            if (line.StartsWith('|'))
            {
                CloseList(html, ref inList);
                html.Append("<p class=\"global-rule\">").Append(Inline(line)).Append("</p>");
                continue;
            }

            CloseList(html, ref inList);
            html.Append("<p>").Append(Inline(line)).Append("</p>");
        }

        CloseList(html, ref inList);
        if (inCode)
            html.Append("</code></pre>");

        return html.ToString();
    }

    private static void CloseList(StringBuilder html, ref bool inList)
    {
        if (!inList)
            return;

        html.Append("</ul>");
        inList = false;
    }

    /// <summary>Inline `code`, **bold** and http(s) links, applied to already-encoded text.</summary>
    private static string Inline(string text)
    {
        var encoded = Encode(text);
        encoded = System.Text.RegularExpressions.Regex.Replace(
            encoded, "`([^`]+)`", "<code>$1</code>");
        encoded = System.Text.RegularExpressions.Regex.Replace(
            encoded, @"\*\*([^*]+)\*\*", "<strong>$1</strong>");
        encoded = System.Text.RegularExpressions.Regex.Replace(
            encoded, @"\[([^\]]+)\]\((https?://[^)\s]+)\)",
            m => $"<a href=\"{m.Groups[2].Value}\" rel=\"noopener noreferrer\">{m.Groups[1].Value}</a>");
        return encoded;
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
