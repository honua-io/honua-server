// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Eval;
using Xunit;

namespace Honua.Server.Tests.Features.Eval;

/// <summary>
/// End-to-end operator-workflow eval harness. Drives the canonical process runtime and
/// its gRPC / OGC API Processes / GeoServices GPServer adapters through a fixture-backed
/// scenario suite and emits the versioned report consumed by honua-devops-31.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.OperatorEval, Protocols.Grpc, Protocols.OgcApiProcesses, Protocols.GPServer)]
public sealed class EvalHarnessTests : IClassFixture<EvalHarnessFixture>
{
    private readonly EvalHarnessFixture _fixture;

    /// <summary>Creates a new instance bound to the shared class-scoped fixture.</summary>
    public EvalHarnessTests(EvalHarnessFixture fixture)
    {
        _fixture = fixture ?? throw new ArgumentNullException(nameof(fixture));
    }

    /// <summary>Analysis-mode scenario: buffer seeded places and summarize overlap with roads.</summary>
    [IntegrationTest]
    [Operation(Operations.ContractTesting)]
    public async Task AnalysisBufferPlaces_PassesEndToEnd()
    {
        await RunScenarioAsync("analysis-buffer-places");
    }

    /// <summary>Publish-mode scenario: refresh the roads layer publication.</summary>
    [IntegrationTest]
    [Operation(Operations.ContractTesting)]
    public async Task PublishRefreshRoads_PassesEndToEnd()
    {
        await RunScenarioAsync("publish-refresh-roads");
    }

    /// <summary>Package-mode scenario: compose a tsunami-evacuation operations map.</summary>
    [IntegrationTest]
    [Operation(Operations.ContractTesting)]
    public async Task PackageMapTsunami_PassesEndToEnd()
    {
        await RunScenarioAsync("package-map-tsunami");
    }

    /// <summary>
    /// GPServer parity must stay honest until the adapter can bind eval scenarios to a
    /// formal GP task catalog.
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.ContractTesting)]
    public async Task AnalysisBufferPlaces_RecordsGpServerProbeAsSkippedUntilTaskBindingExists()
    {
        var scenario = EvalScenarioLoader.LoadById("analysis-buffer-places");
        var result = await _fixture.Runner.RunAsync(scenario, CancellationToken.None);

        var gpServerProbe = result.ProtocolParity.Probes
            .Single(probe => probe.Protocol == Protocols.GPServer);

        gpServerProbe.Assertion.Should().Be("submit-job-surface");
        gpServerProbe.Status.Should().Be(EvalStageStatus.Skipped);
        gpServerProbe.Outcome.Should().Be("task-resolution-unavailable");
    }

    private async Task RunScenarioAsync(string scenarioId)
    {
        var scenario = EvalScenarioLoader.LoadById(scenarioId);
        var result = await _fixture.Runner.RunAsync(scenario, CancellationToken.None);
        _fixture.Record(result);

        // Phase 1: execution-engine and publish-surface stages are intentionally
        // Skipped; only outright Failed scenarios break the gate.
        result.Status.Should().NotBe(EvalOverallStatus.Failed,
            because: $"scenario '{scenarioId}' reported a failed stage: " +
                     string.Join(", ", result.Stages
                         .Where(s => s.Status == EvalStageStatus.Failed)
                         .Select(s => $"{s.Stage}({s.Reason})")));
    }
}
