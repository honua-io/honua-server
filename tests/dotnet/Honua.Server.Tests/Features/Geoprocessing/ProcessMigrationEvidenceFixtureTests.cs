// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Geoprocessing;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Geoprocessing;

/// <summary>
/// Keeps the process migration evidence fixture scaffold aligned with the
/// server-side process classification contract.
/// </summary>
[Protocol(TestProtocols.OgcApiProcesses)]
public sealed class ProcessMigrationEvidenceFixtureTests
{
    private readonly BuiltInProcessCatalog _catalog = new();

    [UnitTest]
    [Operation(Operations.ProcessExecution)]
    public void Fixture_SupportedProcessIds_AreAutomatedProjectedProcesses()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(ResolveRepoFile(
            "tests", "fixtures", "process-migration", "vector-process-parity-fixture.json")));

        fixture.RootElement.GetProperty("schemaVersion").GetString()
            .Should().Be("2026-05-18.process-migration.fixture.v1");

        var processIds = fixture.RootElement.GetProperty("supportedProcessIds")
            .EnumerateArray()
            .Select(element => element.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();

        processIds.Should().Contain(["geometry.buffer", "geometry.clip", "geometry.intersect", "geometry.project"]);

        foreach (var processId in processIds)
        {
            var definition = _catalog.GetProcess(processId);
            definition.Should().NotBeNull();

            var classification = ProcessMigrationEvidenceClassifier.Classify(definition!);
            classification.AutomationTier.Should().Be(ProcessMigrationAutomationTier.Automated);
            classification.IsProjectedThroughOgcApiProcesses.Should().BeTrue();
        }
    }

    [UnitTest]
    [Operation(Operations.JobResults)]
    public void EvidenceArtifactScaffold_DefinesProtocolResultRouteContracts()
    {
        using var artifact = JsonDocument.Parse(File.ReadAllText(ResolveRepoFile(
            "tests", "fixtures", "process-migration", "expected-evidence-artifact.json")));

        artifact.RootElement.GetProperty("schemaVersion").GetString()
            .Should().Be("2026-05-18.process-migration.evidence.v1");
        artifact.RootElement.GetProperty("status").GetString().Should().Be("scaffold");

        var contract = artifact.RootElement.GetProperty("resultContract");
        contract.GetProperty("ogcApiProcesses").GetProperty("resultsRoute").GetString()
            .Should().Be("/ogc/processes/jobs/{jobId}/results");
        contract.GetProperty("geoServicesGpServer").GetProperty("resultsRoute").GetString()
            .Should().Be("/rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/results/{paramName}");

        var checks = artifact.RootElement.GetProperty("checks").EnumerateArray()
            .Select(element => element.GetString())
            .ToArray();
        checks.Should().Contain("unsupported-heavyweight-classification");
        checks.Should().Contain("destructive-approval-classification");
    }

    private static string ResolveRepoFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Unable to locate '{string.Join("/", segments)}'.");
    }
}
