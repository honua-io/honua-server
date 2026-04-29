// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Server.Features.Geoprocessing;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Eval;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Xunit;

namespace Honua.Server.Tests.Features.Eval;

/// <summary>
/// End-to-end operator-workflow eval harness. Drives the canonical process runtime and
/// its gRPC / OGC API Processes / GeoServices GPServer adapters through a fixture-backed
/// scenario suite and emits the versioned report consumed by honua-devops-31.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.OperatorEval, TestProtocols.Grpc, TestProtocols.OgcApiProcesses, TestProtocols.GPServer)]
public sealed class EvalHarnessTests : IClassFixture<EvalHarnessFixture>
{
    private readonly EvalHarnessFixture _fixture;

    /// <summary>Creates a new instance bound to the shared class-scoped fixture.</summary>
    public EvalHarnessTests(EvalHarnessFixture fixture)
    {
        _fixture = fixture ?? throw new ArgumentNullException(nameof(fixture));
    }

    /// <summary>
    /// Runs every scenario discovered under <c>tests/dotnet/eval/scenarios/</c> so that adding
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
    /// GPServer parity probes should target a real published task and then report either
    /// acceptance or an environmental skip when the durable job substrate is unavailable.
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.ContractTesting)]
    public async Task AnalysisBufferPlaces_RecordsGpServerProbeAgainstPublishedTaskSurface()
    {
        var scenario = EvalScenarioLoader.LoadById("analysis-buffer-places");
        var result = await _fixture.Runner.RunAsync(scenario, CancellationToken.None);

        var gpServerProbe = result.ProtocolParity.Probes
            .Single(probe => probe.Protocol == TestProtocols.GPServer);

        gpServerProbe.Assertion.Should().Be("submit-job-surface");
        gpServerProbe.Outcome.Should().BeOneOf("matched-acceptance", "service-unavailable", "authorization-required");
        gpServerProbe.Status.Should().Be(
            gpServerProbe.Outcome == "matched-acceptance"
                ? EvalStageStatus.Passed
                : EvalStageStatus.Skipped);
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
                .Single(probe => probe.Protocol == TestProtocols.OgcApiProcesses);

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
    /// SubmitJob is skipped because it is the RPC that actually enforces the
    /// approval gate. Without the split, DryRun would record a synthetic skip and the
    /// harness would never observe a real artifact-kind mismatch against the seeded
    /// runtime for approval-gated plans.
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.ContractTesting)]
    public async Task ApprovalRequiredScenario_RunsDryRun_SkipsOnlySubmitJob()
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

            var submit = result.Stages.Single(stage => stage.Stage == EvalStageKind.SubmitJob);
            submit.Status.Should().Be(EvalStageStatus.Skipped,
                because: "SubmitJob is the execution-only RPC that enforces EnsureApproved");
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
        var timeoutFixture = new WebAppFixture()
            .ReplaceService<IGeoprocessingJobService>(CreateProtocolTimeoutJobService());

        try
        {
            await timeoutFixture.InitializeAsync();
            // The test job service stalls only protocol HTTP submissions, making the
            // timeout path deterministic even when the real runtime would immediately
            // return an environmental skip such as Redis unavailable.
            timeoutFixture.Client.Timeout = TimeSpan.FromMilliseconds(25);

            var runner = new EvalRunner(timeoutFixture, new LocalSeedFixtureSource());
            var scenario = EvalScenarioLoader.LoadById("analysis-buffer-places");

            var result = await runner.RunAsync(scenario, CancellationToken.None);

            result.Should().NotBeNull();

            var ogcProbe = result.ProtocolParity.Probes
                .Single(probe => probe.Protocol == TestProtocols.OgcApiProcesses);
            ogcProbe.Status.Should().Be(EvalStageStatus.Failed);
            ogcProbe.Outcome.Should().Be("http-timeout");

            var gpServerProbe = result.ProtocolParity.Probes
                .Single(probe => probe.Protocol == TestProtocols.GPServer);
            gpServerProbe.Status.Should().Be(EvalStageStatus.Failed);
            gpServerProbe.Outcome.Should().Be("http-timeout");
        }
        finally
        {
            await timeoutFixture.DisposeAsync();
        }
    }

    /// <summary>
    /// Caller cancellation of the scenario's <see cref="CancellationToken"/> must abort
    /// <see cref="EvalRunner.RunAsync"/> with an <see cref="OperationCanceledException"/>
    /// rather than being swallowed into <c>Failed(grpc-cancelled)</c> stage outcomes.
    /// gRPC surfaces caller cancellation as <c>StatusCode.Cancelled</c>, so each gRPC
    /// stage helper must distinguish caller cancel from a server-side cancel status
    /// and propagate the former unchanged.
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.ContractTesting)]
    public async Task RunAsync_WhenCallerTokenIsCancelled_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var scenario = EvalScenarioLoader.LoadById("analysis-buffer-places");

        var act = async () => await _fixture.Runner.RunAsync(scenario, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    /// Enumerates every scenario id under <c>tests/dotnet/eval/scenarios/</c> so the harness
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

    private static IGeoprocessingJobService CreateProtocolTimeoutJobService()
    {
        var jobService = Substitute.For<IGeoprocessingJobService>();
        jobService
            .ValidatePlan(Arg.Any<AnalysisPlan>(), Arg.Any<ClaimsPrincipal>())
            .Returns(new PlanValidationResult { IsExecutable = true });
        jobService
            .DryRunPlan(Arg.Any<AnalysisPlan>(), Arg.Any<ClaimsPrincipal>())
            .Returns(new DryRunResult
            {
                EstimatedDurationSeconds = 1,
                EstimatedArtifacts = [ArtifactKind.FeatureLayer, ArtifactKind.Report]
            });
        jobService
            .SubmitJobAsync(
                Arg.Any<AnalysisPlan>(),
                Arg.Any<string?>(),
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<IReadOnlyDictionary<string, string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var protocolMetadata = call.ArgAt<IReadOnlyDictionary<string, string>?>(3);
                var cancellationToken = call.ArgAt<CancellationToken>(4);
                if (protocolMetadata?.TryGetValue("submittedVia", out var submittedVia) == true
                    && (string.Equals(submittedVia, "OGC-API-Processes", StringComparison.Ordinal)
                        || string.Equals(submittedVia, "GPServer", StringComparison.Ordinal)))
                {
                    return DelayProtocolSubmitAsync(cancellationToken);
                }

                return Task.FromResult(CreateQueuedExecutionJobRecord());
            });

        return jobService;
    }

    private static async Task<ExecutionJobRecord> DelayProtocolSubmitAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        return CreateQueuedExecutionJobRecord();
    }

    private static ExecutionJobRecord CreateQueuedExecutionJobRecord()
    {
        var now = DateTimeOffset.UtcNow;
        return new ExecutionJobRecord
        {
            OperationId = $"eval-timeout-{Guid.NewGuid():N}",
            Status = ExecutionJobStatus.Queued,
            CreatedAt = now,
            UpdatedAt = now,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "test",
                WorkloadName = "operator-eval-timeout"
            }
        };
    }

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
