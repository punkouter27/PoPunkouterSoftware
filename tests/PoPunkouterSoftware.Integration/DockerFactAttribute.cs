using System.Diagnostics;

namespace PoPunkouterSoftware.Integration;

/// <summary>
/// Whether a Docker daemon answers, probed once per test run with <c>docker version</c>. The Testcontainers
/// (Azurite) tests need one; CI's ubuntu-latest runner has it, a laptop may not.
/// </summary>
public static class Docker
{
    public const string SkipReason =
        "Docker is not running, so the Testcontainers Azurite tests are skipped. Start Docker Desktop and rerun "
        + "to include them (CI always runs them).";

    private static readonly Lazy<bool> Probe = new(() =>
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("docker", "version --format {{.Server.Version}}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null)
                return false;
            if (!process.WaitForExit(TimeSpan.FromSeconds(15)))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // no docker CLI on PATH
        }
    });

    public static bool IsAvailable => Probe.Value;
}

/// <summary>A <see cref="FactAttribute"/> that skips, with one clear reason, when Docker is not running.</summary>
public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute()
    {
        if (!Docker.IsAvailable)
            Skip = Docker.SkipReason;
    }
}
