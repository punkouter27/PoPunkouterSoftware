using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// Package freshness per repo, standing in for Dependabot, which is off on every Po repo (so
/// <see cref="WebService.OpenSecurityAlerts"/> is always null). Every Po .NET repo pins its
/// versions centrally, so one file per repo is the whole manifest: <c>Directory.Packages.props</c>
/// is read through the GitHub contents API, each package's newest stable version comes from
/// nuget.org's flat container, and one OSV.dev batch query finds pinned versions with a known
/// advisory. Both public APIs are keyless; the PAT goes to GitHub only.
/// </summary>
public partial class AzureReportService
{
    private async Task<Dictionary<string, PackageHealthInfo>> GetPackageHealthAsync(
        IEnumerable<string> fullNames, CancellationToken ct)
    {
        var github = _httpClientFactory.CreateClient("github");
        var manifests = await BoundedParallelAsync(
            fullNames.Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase), maxConcurrency: 6,
            async name => (name, packages: await FetchCentralPackagesAsync(github, name, ct)), ct);
        var withPackages = manifests.Where(m => m.packages.Count > 0).ToList();
        if (withPackages.Count == 0)
            return new(StringComparer.OrdinalIgnoreCase);

        var pinned = withPackages.SelectMany(m => m.packages).Distinct().ToList();
        var http = _httpClientFactory.CreateClient(PublicApiClientName);
        var latest = (await BoundedParallelAsync(
                pinned.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase), maxConcurrency: 8,
                async id => (id, version: await FetchLatestStableAsync(http, id, ct)), ct))
            .Where(r => r.version is not null)
            .ToDictionary(r => r.id, r => r.version!, StringComparer.OrdinalIgnoreCase);
        var advisories = await FetchAdvisoriesAsync(http, pinned, ct);

        return withPackages.ToDictionary(m => m.name, m => new PackageHealthInfo
        {
            Checked = m.packages.Count,
            Outdated = m.packages
                .Where(p => latest.TryGetValue(p.Id, out var l) && IsNewer(l, p.Version))
                .Select(p => $"{p.Id} {p.Version} → {latest[p.Id]}")
                .ToList(),
            Vulnerable = m.packages
                .Where(advisories.ContainsKey)
                .Select(p => $"{p.Id} {p.Version} ({string.Join(", ", advisories[p].Take(2))})")
                .ToList(),
        }, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<List<(string Id, string Version)>> FetchCentralPackagesAsync(HttpClient github, string fullName, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{fullName}/contents/Directory.Packages.props");
            req.Headers.Accept.ParseAdd("application/vnd.github.raw+json");
            using var resp = await github.SendAsync(req, ct);
            return resp.IsSuccessStatusCode ? ParseCentralPackages(await resp.Content.ReadAsStringAsync(ct)) : [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Directory.Packages.props unreadable for {Repo}", fullName);
            return [];
        }
    }

    /// <summary>
    /// Pure: <c>PackageVersion Include/Version</c> pairs. A version that is an MSBuild property
    /// (<c>$(AspNetVersion)</c>) is skipped — it cannot be resolved without evaluating the build.
    /// </summary>
    public static List<(string Id, string Version)> ParseCentralPackages(string xml)
    {
        try
        {
            return XDocument.Parse(xml).Descendants()
                .Where(e => e.Name.LocalName == "PackageVersion")
                .Select(e => (Id: (string?)e.Attribute("Include") ?? "", Version: (string?)e.Attribute("Version") ?? ""))
                .Where(p => p.Id.Length > 0 && p.Version.Length > 0 && !p.Version.Contains('$'))
                .Distinct()
                .ToList();
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }
    }

    private static async Task<string?> FetchLatestStableAsync(HttpClient http, string id, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await http.GetStringAsync(
                $"https://api.nuget.org/v3-flatcontainer/{id.ToLowerInvariant()}/index.json", ct));
            return doc.RootElement.GetProperty("versions").EnumerateArray()
                .Select(v => v.GetString() ?? "")
                .LastOrDefault(v => v.Length > 0 && !v.Contains('-'));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>One OSV.dev batch call for every pinned (package, version): advisory ids per package.</summary>
    private async Task<Dictionary<(string Id, string Version), List<string>>> FetchAdvisoriesAsync(
        HttpClient http, List<(string Id, string Version)> pinned, CancellationToken ct)
    {
        var result = new Dictionary<(string, string), List<string>>();
        try
        {
            using var resp = await http.PostAsJsonAsync("https://api.osv.dev/v1/querybatch", new
            {
                queries = pinned.Select(p => new { package = new { ecosystem = "NuGet", name = p.Id }, version = p.Version }),
            }, ct);
            if (!resp.IsSuccessStatusCode)
                return result;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var i = 0;
            foreach (var entry in doc.RootElement.GetProperty("results").EnumerateArray())
            {
                if (i < pinned.Count && entry.TryGetProperty("vulns", out var vulns) && vulns.GetArrayLength() > 0)
                    result[pinned[i]] = vulns.EnumerateArray().Select(v => v.GetProperty("id").GetString() ?? "").ToList();
                i++;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "OSV.dev advisory lookup failed");
        }
        return result;
    }

    /// <summary>Pure: numeric comparison of two release versions; a prerelease pin is never "outdated".</summary>
    public static bool IsNewer(string candidate, string current) =>
        !current.Contains('-')
        && Version.TryParse(candidate.Split('+')[0], out var c)
        && Version.TryParse(current.Split('+')[0], out var v)
        && c > v;
}
