// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Eval;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

    /// <summary>
    /// Runs every scenario discovered under <c>tests/Eval/scenarios/</c> so that adding
    /// a new JSON scenario automatically contributes to <c>eval-report.json</c> without
    /// requiring a code change here.
    /// </summary>
    [Theory]
    [MemberData(nameof(DiscoveredScenarioIds))]
    [Operation(Operations.ContractTesting)]
    public async Task Scenario_PassesEndToEnd(string scenarioId)
    {
        await RunScenarioAsync(scenarioId);
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

    /// <summary>
    /// Dry-run validation must fail when the server reports artifact kinds beyond the
    /// scenario's declared set. Subset-only comparison would silently accept drift in
    /// the canonical runtime's artifact surface, weakening the eval gate's contract.
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.ContractTesting)]
    public async Task DryRun_UnexpectedArtifactKinds_FailsScenario()
    {
        var baseScenario = EvalScenarioLoader.LoadById("analysis-buffer-places");
        var scenario = baseScenario with
        {
            ExpectedOutcome = baseScenario.ExpectedOutcome with
            {
                EstimatedArtifactKinds = [ArtifactKind.FeatureLayer]
            }
        };

        var result = await _fixture.Runner.RunAsync(scenario, CancellationToken.None);

        var dryRun = result.Stages.Single(stage => stage.Stage == EvalStageKind.DryRun);
        dryRun.Status.Should().Be(EvalStageStatus.Failed);
        dryRun.Reason.Should().Be("artifact-kinds-unexpected");
    }

    /// <summary>
    /// OGC protocol parity must treat a 403 approval-required response as a matched
    /// rejection when the scenario itself expects <c>RequiresApproval=true</c>. Without
    /// this, any future approval-gated scenario would incorrectly fail parity even
    /// though the OGC adapter matches the canonical runtime's approval gate exactly.
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.ContractTesting)]
    public async Task OgcProbe_ApprovalRequiredScenario_MatchesApprovalRejection()
    {
        var approvalFixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IOperatorApprovalEvaluator>();
                services.AddSingleton<IOperatorApprovalEvaluator>(new DestructiveOnlyApprovalEvaluator());
            });

        try
        {
            await approvalFixture.InitializeAsync();
            var runner = new EvalRunner(approvalFixture, new LocalSeedFixtureSource());
            var scenario = BuildDestructiveApprovalScenario();

            var result = await runner.RunAsync(scenario, CancellationToken.None);

            var ogcProbe = result.ProtocolParity.Probes
                .Single(probe => probe.Protocol == Protocols.OgcApiProcesses);

            ogcProbe.Assertion.Should().Be("plan-shape-accepted");
            ogcProbe.Outcome.Should().Be("matched-approval-required");
            ogcProbe.Status.Should().Be(EvalStageStatus.Passed);
        }
        finally
        {
            await approvalFixture.DisposeAsync();
        }
    }

    /// <summary>
    /// An approval-gated scenario must still exercise the canonical DryRun contract
    /// (a Read operation that does not enforce executability or approval) while only
    /// SubmitPlanJob is skipped because it is the RPC that actually enforces the
    /// approval gate. Without the split, DryRun would record a synthetic skip and the
    /// harness would never observe a real artifact-kind mismatch against the seeded
    /// runtime for approval-gated plans.
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.ContractTesting)]
    public async Task ApprovalRequiredScenario_RunsDryRun_SkipsOnlySubmitPlanJob()
    {
        var approvalFixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IOperatorApprovalEvaluator>();
                services.AddSingleton<IOperatorApprovalEvaluator>(new DestructiveOnlyApprovalEvaluator());
            });

        try
        {
            await approvalFixture.InitializeAsync();
            var runner = new EvalRunner(approvalFixture, new LocalSeedFixtureSource());
            var scenario = BuildDestructiveApprovalScenario();

            var result = await runner.RunAsync(scenario, CancellationToken.None);

            var dryRun = result.Stages.Single(stage => stage.Stage == EvalStageKind.DryRun);
            dryRun.Status.Should().Be(EvalStageStatus.Passed,
                because: "DryRun is authorized as a Read operation and must still run against approval-gated plans");
            dryRun.Reason.Should().BeNull();

            var submit = result.Stages.Single(stage => stage.Stage == EvalStageKind.SubmitPlanJob);
            submit.Status.Should().Be(EvalStageStatus.Skipped,
                because: "SubmitPlanJob is the execution-only RPC that enforces EnsureApproved");
            submit.Reason.Should().Be("plan-approval-required");
        }
        finally
        {
            await approvalFixture.DisposeAsync();
        }
    }

    /// <summary>
    /// HTTP parity probes must convert transport-level timeouts (e.g. <c>HttpClient.Timeout</c>
    /// firing) into a deterministic <c>Failed(http-timeout)</c> probe rather than letting
    /// the <c>TaskCanceledException</c> propagate and abort the scenario. This preserves
    /// <see cref="EvalRunner.RunAsync"/>'s no-throw reporting contract.
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.ContractTesting)]
    public async Task Probes_WhenHttpClientTimeoutFires_ReportFailedWithoutAbortingScenario()
    {
        var timeoutFixture = new WebAppFixture();

        try
        {
            await timeoutFixture.InitializeAsync();
            // Forcing an extreme client-level timeout guarantees the HTTP probes will
            // surface OperationCanceledException unrelated to the caller's token. The
            // fix converts that into a Failed(http-timeout) probe instead of aborting
            // RunAsync.
            timeoutFixture.Client.Timeout = TimeSpan.FromTicks(1);

            var runner = new EvalRunner(timeoutFixture, new LocalSeedFixtureSource());
            var scenario = EvalScenarioLoader.LoadById("analysis-buffer-places");

            var result = await runner.RunAsync(scenario, CancellationToken.None);

            result.Should().NotBeNull();

            var ogcProbe = result.ProtocolParity.Probes
                .Single(probe => probe.Protocol == Protocols.OgcApiProcesses);
            ogcProbe.Status.Should().Be(EvalStageStatus.Failed);
            ogcProbe.Outcome.Should().Be("http-timeout");

            var gpServerProbe = result.ProtocolParity.Probes
                .Single(probe => probe.Protocol == Protocols.GPServer);
            gpServerProbe.Status.Should().Be(EvalStageStatus.Failed);
            gpServerProbe.Outcome.Should().Be("http-timeout");
        }
        finally
        {
            await timeoutFixture.DisposeAsync();
        }
    }

    /// <summary>
    /// Enumerates every scenario id under <c>tests/Eval/scenarios/</c> so the harness
    /// suite expands automatically as the corpus grows.
    /// </summary>
    public static IEnumerable<object[]> DiscoveredScenarioIds()
    {
        foreach (var id in EvalScenarioLoader.DiscoverScenarioIds())
        {
            yield return [id];
        }
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

    private static EvalScenario BuildDestructiveApprovalScenario() => new()
    {
        Id = "approval-required-delete-features",
        Name = "Delete-features plan that must clear the approval gate",
        Mode = EvalScenarioMode.Analysis,
        FixtureProfile = "ogc",
        Intent = new EvalIntentSpec
        {
            IntentId = "intent-approval-required-delete-features",
            Goal = "Bulk-delete features from the roads layer (destructive; requires approval).",
            Mode = "analysis",
            RequestedOutputs = [ArtifactKind.Scalar],
            Inputs = ["roads"],
            AssumptionPolicy = AssumptionPolicy.UseDefaults
        },
        PrecompiledPlan = new EvalPlanSpec
        {
            PlanId = "plan-approval-required-delete-features",
            IntentId = "intent-approval-required-delete-features",
            Steps =
            [
                new EvalPlanStepSpec
                {
                    StepId = "delete-roads",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "data-management.delete-features",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "0",
                        ["where"] = "OBJECTID > 0"
                    }
                }
            ],
            Outputs = [ArtifactKind.Scalar]
        },
        ExpectedOutcome = new EvalExpectedOutcome
        {
            IsExecutable = true,
            RequiresApproval = true,
            EstimatedArtifactKinds = [ArtifactKind.Scalar]
        }
    };

    private sealed class DestructiveOnlyApprovalEvaluator : IOperatorApprovalEvaluator
    {
        public ApprovalRequirement Evaluate(ClaimsPrincipal principal, OperatorAuthorizationRequest request)
            => request.IsDestructive
                ? ApprovalRequirement.Required(
                    $"operator.destructive.{request.ResourceType.ToString().ToLowerInvariant()}",
                    "destructive-action-requires-approval")
                : ApprovalRequirement.NotRequired();
    }
}
