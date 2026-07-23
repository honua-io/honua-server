using System.Diagnostics;
using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests.FeatureCatalog;

/// <summary>
/// Guards the capability-to-proving-test-to-CI-shard crosswalk used by the
/// report-only impact selector.
/// </summary>
public sealed class CapabilityImpactCompletenessTests
{
    [Fact]
    public void CapabilityImpactGraph_ShouldBeComplete()
    {
        var repositoryRoot = FindRepositoryRoot();
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "python" : "python3",
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("scripts/ci/capability-impact.py");
        startInfo.ArgumentList.Add("validate");

        using var process = Process.Start(startInfo);
        process.Should().NotBeNull();
        var standardOutput = process!.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();

        process.ExitCode.Should().Be(0, $"capability impact validation must pass. stdout: {standardOutput}; stderr: {standardError}");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Join(directory.FullName, "Honua.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }
}
