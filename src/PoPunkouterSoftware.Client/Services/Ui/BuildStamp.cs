using System.Reflection;

namespace PoPunkouterSoftware.Client;

/// <summary>
/// The build's YYMMDD stamp, from the <c>BuildVersion</c> assembly metadata the client csproj
/// writes at build time. Read once: the assembly cannot change under a running tab.
/// </summary>
public static class BuildStamp
{
    public static string Value { get; } =
        typeof(BuildStamp).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildVersion")?.Value
        ?? "unknown";
}
