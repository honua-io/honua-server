// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;

namespace Honua.Architecture.Tests;

/// <summary>
/// Pins the runtime-image fixes for the open Trivy rows that a package bump or a removal can close:
/// CVE-2026-111943 (Lambda Runtime Interface Emulator), CVE-2026-85091 (zlib1g), and CVE-2021-31879 (wget).
/// </summary>
public sealed class RuntimeImageCveTests
{
    private const string PackageRevision = "ARG RUNTIME_PACKAGE_REVISION=20261030";

    [ArchitectureTest]
    public void RuntimeStages_DoNotInstallWgetAndProbeWithBash()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        foreach (var relativePath in new[] { "Dockerfile", "docker/Dockerfile.aot" })
        {
            var dockerfile = File.ReadAllText(Path.Join(repositoryRoot, relativePath));
            var runtimeStage = dockerfile[dockerfile.LastIndexOf("AS runtime", StringComparison.Ordinal)..];

            runtimeStage.Should().NotContain("wget", $"{relativePath} runtime stage must not install or call wget");
            runtimeStage.Should().Contain(PackageRevision, relativePath);
            runtimeStage.Should().Contain("apt-get upgrade -y", relativePath);
            runtimeStage.Should().Contain("dpkg-query -W -f '${Version}' zlib1g", relativePath);
            runtimeStage.Should().NotContain("dpkg-query -W -f '$${Version}'",
                $"{relativePath} must pass dpkg-query ${{Version}}; a doubled dollar emits a leading dollar and compare-versions rejects the epoch");
            runtimeStage.Should().Contain("HEALTHCHECK", relativePath);
            runtimeStage.Should().Contain("/dev/tcp/127.0.0.1/8080", relativePath);
            runtimeStage.Should().Contain("GET /healthz/live HTTP/1.0", relativePath);
        }
    }

    [ArchitectureTest]
    public void LambdaJitImage_DropsTheEmulatorFromThePublishedStageOnly()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var dockerfile = File.ReadAllText(Path.Join(repositoryRoot, "docker", "Dockerfile.lambda"));

        const string localEmulation = "FROM runtime AS local-emulation";
        var emulationAt = dockerfile.IndexOf(localEmulation, StringComparison.Ordinal);
        emulationAt.Should().BeGreaterThan(0);

        var publishedAt = dockerfile.IndexOf("FROM runtime\n", emulationAt + localEmulation.Length, StringComparison.Ordinal);
        publishedAt.Should().BeGreaterThan(emulationAt);

        var emulationStage = dockerfile[emulationAt..publishedAt];
        emulationStage.Should().NotContain("aws-lambda-rie",
            "the local-emulation target keeps the base image emulator by not deleting it");

        var publishedStage = dockerfile[publishedAt..];
        publishedStage.Should().Contain("rm -f /usr/local/bin/aws-lambda-rie");
        publishedStage.Should().NotContain("HEALTHCHECK",
            "the Lambda image has no Docker healthcheck; do not add a package to probe it");
    }

    [ArchitectureTest]
    public void LambdaAotRuntime_RefreshesTheSameAptRevisionAsTheServingImages()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var dockerfile = File.ReadAllText(Path.Join(repositoryRoot, "docker", "Dockerfile.lambda.aot"));
        var runtimeStage = dockerfile[dockerfile.LastIndexOf("AS runtime", StringComparison.Ordinal)..];

        runtimeStage.Should().Contain(PackageRevision);
        runtimeStage.Should().Contain("apt-get upgrade -y");
        runtimeStage.Should().Contain("dpkg-query -W -f '${Version}' zlib1g");
        runtimeStage.Should().NotContain("dpkg-query -W -f '$${Version}'");
        runtimeStage.Should().NotContain("wget");
    }

    [ArchitectureTest]
    public void HonuaServerHealthchecks_DoNotCallWget()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var offenders = new List<string>();
        var composeFiles = Directory.EnumerateFiles(repositoryRoot, "docker-compose*.yml", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(Path.Join(repositoryRoot, "docker"), "*compose*.yml", SearchOption.AllDirectories));
        foreach (var path in composeFiles)
        {
            var relativePath = Path.GetRelativePath(repositoryRoot, path);
            var lines = File.ReadAllLines(Path.Join(repositoryRoot, relativePath));
            string? image = null;
            var inHonuaService = false;
            foreach (var line in lines)
            {
                if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && line.EndsWith(':') && !line.StartsWith('#'))
                {
                    inHonuaService = false;
                    image = null;
                }

                var trimmed = line.Trim();
                if (trimmed.StartsWith("image:", StringComparison.Ordinal))
                {
                    image = trimmed["image:".Length..].Trim().Trim('"');
                    inHonuaService = IsHonuaServerImage(image);
                }
                else if (trimmed.StartsWith("build:", StringComparison.Ordinal) && image is null)
                {
                    inHonuaService = true;
                }

                if (!inHonuaService || !trimmed.StartsWith("test:", StringComparison.Ordinal))
                {
                    continue;
                }

                if (trimmed.Contains("wget", StringComparison.Ordinal)
                    && trimmed.Contains("healthz", StringComparison.Ordinal))
                {
                    offenders.Add($"{relativePath}: wget {trimmed}");
                }

                // Compose interpolates $status on the host before the container
                // starts. The file must contain $$status so the shell still sees $status.
                if (trimmed.Contains("/dev/tcp/", StringComparison.Ordinal)
                    && trimmed.Contains("healthz", StringComparison.Ordinal)
                    && System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"(?<!\$)\$status"))
                {
                    offenders.Add($"{relativePath}: compose interpolates the status variable");
                }
            }
        }

        offenders.Should().BeEmpty("Honua server containers no longer install wget; probe with bash /dev/tcp");
    }

    private static bool IsHonuaServerImage(string image)
    {
        if (image.Contains("nginx", StringComparison.OrdinalIgnoreCase)
            || image.Contains("caddy", StringComparison.OrdinalIgnoreCase)
            || image.Contains("postgis", StringComparison.OrdinalIgnoreCase)
            || image.Contains("postgres", StringComparison.OrdinalIgnoreCase)
            || image.Contains("redis", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return image.Contains("honua", StringComparison.OrdinalIgnoreCase)
            || image.Contains("HONUA_", StringComparison.Ordinal)
            || image.Contains("${", StringComparison.Ordinal);
    }
}
