// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;
using FluentAssertions;

namespace Honua.Architecture.Tests;

public sealed partial class RuntimeImageOwnershipTests
{
    [ArchitectureTest]
    public void ServingImages_KeepApplicationFilesRootOwned()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        foreach (var relativePath in new[] { "Dockerfile", "docker/Dockerfile.aot" })
        {
            var dockerfile = File.ReadAllText(Path.Join(repositoryRoot, relativePath));
            var runtimeStage = dockerfile[dockerfile.LastIndexOf("FROM chiseled-base AS runtime", StringComparison.Ordinal)..];

            runtimeStage.Should().Contain("COPY --from=build /app .", relativePath);
            runtimeStage.Should().NotContain(
                "--chown=1001",
                "{0} must not hand application files to the runtime user, who could then rewrite them",
                relativePath);
            runtimeStage.Should().Contain("USER 1001:1001", relativePath);
            // Scene generation defaults to a path under the content root (/app); the image points it at
            // the provisioned writable storage instead.
            runtimeStage.Should().Contain(
                "SceneGeneration__OutputRoot=/var/lib/honua/storage/scenes-generated",
                relativePath);
        }
    }

    [ArchitectureTest]
    public void GdalWorkerImage_KeepsApplicationFilesRootOwned()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var dockerfile = File.ReadAllText(Path.Join(repositoryRoot, "docker/worker-gdal/Dockerfile"));
        var runtimeStage = dockerfile[dockerfile.LastIndexOf("FROM native-tools AS runtime", StringComparison.Ordinal)..];

        runtimeStage.Should().Contain("COPY --from=build /app .");
        runtimeStage.Should().NotContain(
            "--chown=1001",
            "the worker's application files and scripts must not be writable by the job user");
        runtimeStage.Should().Contain("chown 1001:1001 /tmp/honua-gdal-worker");
        runtimeStage.Should().NotMatchRegex(@"chown\s+(-R\s+)?(1001|honua)\S*\s[^\n]*/app\b");
        runtimeStage.Should().Contain("USER 1001:1001");
    }

    [ArchitectureTest]
    public void TrivyIgnoreEntries_CarryAnExpiry()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        foreach (var relativePath in new[] { ".trivyignore", ".trivyignore-release" })
        {
            var permanent = File.ReadAllLines(Path.Join(repositoryRoot, relativePath))
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith('#') && !ExpiringEntry().IsMatch(line));

            permanent.Should().BeEmpty(
                "every risk acceptance in {0} must lapse (exp:YYYY-MM-DD) so Trivy reports it again for review",
                relativePath);
        }
    }

    [GeneratedRegex(@"^\S+ exp:\d{4}-\d{2}-\d{2}$")]
    private static partial Regex ExpiringEntry();
}
