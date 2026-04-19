// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;
using Honua.Core.Features.Geoprocessing.Domain;
using Proto = Geospatial.V1;

namespace Honua.TestKit.Eval;

/// <summary>
/// End-to-end eval harness runner. Drives the canonical runtime and protocol
/// adapters (gRPC <see cref="Proto.ProcessService"/>, OGC API Processes, GeoServices
/// GPServer) through a single <see cref="EvalScenario"/> and captures per-stage
/// outcomes into an <see cref="EvalScenarioResult"/>.
/// </summary>
public sealed class EvalRunner
{
    /// <summary>
    /// <see cref="ActivitySource"/> used to emit one span per scenario and per stage.
    /// Downstream observability in CI consumes these spans through the standard
    /// <c>Honua.ServiceDefaults</c> telemetry pipeline when enabled.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new("Honua.Tests.Eval");

    private readonly WebAppFixture _fixture;
    private readonly IEvalFixtureSource _fixtureSource;

    /// <summary>Creates a runner bound to the supplied web host and fixture source.</summary>
    public EvalRunner(WebAppFixture fixture, IEvalFixtureSource fixtureSource)
    {
        _fixture = fixture ?? throw new ArgumentNullException(nameof(fixture));
        _fixtureSource = fixtureSource ?? throw new ArgumentNullException(nameof(fixtureSource));
    }

    /// <summary>
    /// Executes all stages for the given scenario. Stages may be <see cref="EvalStageStatus.Skipped"/>
    /// when upstream capabilities are not yet wired (e.g. execution engine, publish surface).
    /// Never throws for per-stage failures; surface those through the returned outcome.
    /// </summary>
    public async Task<EvalScenarioResult> RunAsync(EvalScenario scenario, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        using var scenarioActivity = ActivitySource.StartActivity("eval.scenario");
        scenarioActivity?.SetTag("eval.scenario.id", scenario.Id);
        scenarioActivity?.SetTag("eval.scenario.mode", scenario.Mode.ToString());
        scenarioActivity?.SetTag("eval.fixture.source", _fixtureSource.Id);

        var overallStopwatch = Stopwatch.StartNew();
        var stages = new List<EvalStageOutcome>(capacity: 12);
        EvalProtocolParityOutcome parity = new();

        using var channel = CreateGrpcChannel();
        var client = new Proto.ProcessService.ProcessServiceClient(channel);
        var grpcHeaders = BuildGrpcHeaders();
        var domainPlan = BuildDomainPlan(scenario.PrecompiledPlan);

        stages.Add(RunCaptureIntent(scenario));
        stages.Add(RunCompilePlan(scenario, domainPlan));

        var validateOutcome = await RunValidatePlanAsync(scenario, client, domainPlan, grpcHeaders, cancellationToken)
            .ConfigureAwait(false);
        stages.Add(validateOutcome.Stage);

        var dryRunOutcome = await RunDryRunAsync(scenario, client, domainPlan, grpcHeaders, cancellationToken)
            .ConfigureAwait(false);
        stages.Add(dryRunOutcome.Stage);

        var parityStopwatch = Stopwatch.StartNew();
        parity = await RunProtocolParityAsync(scenario, validateOutcome.Response, cancellationToken).ConfigureAwait(false);
        parityStopwatch.Stop();
        stages.Add(BuildProtocolParityStageOutcome(parity, parityStopwatch.ElapsedMilliseconds));

        var submitOutcome = await RunSubmitPlanJobAsync(scenario, client, domainPlan, grpcHeaders, cancellationToken)
            .ConfigureAwait(false);
        stages.Add(submitOutcome.Stage);

        stages.Add(BuildSkipOutcome(EvalStageKind.PollJob,
            "execution-engine-pending", "Execution engine not yet wired (see #732)."));
        stages.Add(BuildSkipOutcome(EvalStageKind.GetJobResults,
            "execution-engine-pending", "Result-package retrieval requires the execution engine."));

        stages.Add(BuildModeScopedStage(scenario, EvalStageKind.ComposeMapPackage,
            scenario.Mode is EvalScenarioMode.Package or EvalScenarioMode.Deploy,
            "map-package-surface-pending", "Map package surface defined in #730."));
        stages.Add(BuildModeScopedStage(scenario, EvalStageKind.ComposeAppPackage,
            scenario.ExpectedOutcome.ExpectsAppPackage,
            "app-package-surface-pending", "App package surface defined in #731."));
        stages.Add(BuildModeScopedStage(scenario, EvalStageKind.PromoteDeployment,
            scenario.Mode is EvalScenarioMode.Deploy,
            "deploy-surface-pending", "Deployment promotion surface defined in #732."));

        overallStopwatch.Stop();

        var status = RollupScenarioStatus(stages, parity);

        return new EvalScenarioResult
        {
            Id = scenario.Id,
            Name = scenario.Name,
            Mode = scenario.Mode,
            Status = status,
            Stages = stages,
            ProtocolParity = parity,
            ElapsedMs = overallStopwatch.ElapsedMilliseconds
        };
    }

    // -----------------------------------------------------------------------
    // Stage runners
    // -----------------------------------------------------------------------

    private EvalStageOutcome RunCaptureIntent(EvalScenario scenario)
    {
        using var activity = ActivitySource.StartActivity("eval.stage.capture_intent");
        activity?.SetTag("eval.scenario.id", scenario.Id);

        var stopwatch = Stopwatch.StartNew();
        var intent = BuildDomainIntent(scenario.Intent);
        stopwatch.Stop();

        if (string.IsNullOrWhiteSpace(intent.IntentId) || string.IsNullOrWhiteSpace(intent.Goal))
        {
            return new EvalStageOutcome
            {
                Stage = EvalStageKind.CaptureIntent,
                Status = EvalStageStatus.Failed,
                Reason = "intent-missing-identifiers",
                Detail = "IntentId and Goal are required for a canonical AnalysisIntent.",
                ElapsedMs = stopwatch.ElapsedMilliseconds
            };
        }

        if (intent.Constraints is { SpatialReferenceId: not null } constraints)
        {
            var sridValid = constraints.SpatialReferenceId > 0 && constraints.SpatialReferenceId < 1_000_000;
            if (!sridValid)
            {
                return new EvalStageOutcome
                {
                    Stage = EvalStageKind.CaptureIntent,
                    Status = EvalStageStatus.Failed,
                    Reason = "srid-out-of-range",
                    Detail = $"SpatialReferenceId '{constraints.SpatialReferenceId}' outside recognized EPSG range.",
                    ElapsedMs = stopwatch.ElapsedMilliseconds
                };
            }
        }

        return new EvalStageOutcome
        {
            Stage = EvalStageKind.CaptureIntent,
            Status = EvalStageStatus.Passed,
            ElapsedMs = stopwatch.ElapsedMilliseconds
        };
    }

    private EvalStageOutcome RunCompilePlan(EvalScenario scenario, AnalysisPlan domainPlan)
    {
        using var activity = ActivitySource.StartActivity("eval.stage.compile_plan");
        activity?.SetTag("eval.scenario.id", scenario.Id);

        var stopwatch = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(domainPlan.PlanId)
            || !string.Equals(domainPlan.IntentId, scenario.Intent.IntentId, StringComparison.Ordinal))
        {
            stopwatch.Stop();
            return new EvalStageOutcome
            {
                Stage = EvalStageKind.CompilePlan,
                Status = EvalStageStatus.Failed,
                Reason = "plan-intent-mismatch",
                Detail = "Precompiled plan must bind its IntentId to scenario.intent.intentId.",
                ElapsedMs = stopwatch.ElapsedMilliseconds
            };
        }

        if (domainPlan.Steps.Count == 0)
        {
            stopwatch.Stop();
            return new EvalStageOutcome
            {
                Stage = EvalStageKind.CompilePlan,
                Status = EvalStageStatus.Failed,
                Reason = "plan-empty",
                Detail = "Precompiled plan must contain at least one step.",
                ElapsedMs = stopwatch.ElapsedMilliseconds
            };
        }

        stopwatch.Stop();
        return new EvalStageOutcome
        {
            Stage = EvalStageKind.CompilePlan,
            Status = EvalStageStatus.Passed,
            ElapsedMs = stopwatch.ElapsedMilliseconds
        };
    }

    private async Task<(EvalStageOutcome Stage, Proto.ValidatePlanResponse? Response)> RunValidatePlanAsync(
        EvalScenario scenario,
        Proto.ProcessService.ProcessServiceClient client,
        AnalysisPlan domainPlan,
        Metadata headers,
        CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("eval.stage.validate_plan");
        activity?.SetTag("eval.scenario.id", scenario.Id);
        activity?.SetTag("eval.protocol", "grpc");

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var request = new Proto.ValidatePlanRequest { Plan = ToProtoPlan(domainPlan) };
            var response = await client.ValidatePlanAsync(request, headers, cancellationToken: cancellationToken);
            stopwatch.Stop();

            if (response.IsExecutable != scenario.ExpectedOutcome.IsExecutable)
            {
                return (new EvalStageOutcome
                {
                    Stage = EvalStageKind.ValidatePlan,
                    Status = EvalStageStatus.Failed,
                    Reason = "is-executable-mismatch",
                    Detail = $"Expected IsExecutable={scenario.ExpectedOutcome.IsExecutable} but got {response.IsExecutable} with {response.Violations.Count} violations.",
                    ElapsedMs = stopwatch.ElapsedMilliseconds
                }, response);
            }

            if (response.RequiresApproval != scenario.ExpectedOutcome.RequiresApproval)
            {
                return (new EvalStageOutcome
                {
                    Stage = EvalStageKind.ValidatePlan,
                    Status = EvalStageStatus.Failed,
                    Reason = "requires-approval-mismatch",
                    Detail = $"Expected RequiresApproval={scenario.ExpectedOutcome.RequiresApproval} but got {response.RequiresApproval}.",
                    ElapsedMs = stopwatch.ElapsedMilliseconds
                }, response);
            }

            return (new EvalStageOutcome
            {
                Stage = EvalStageKind.ValidatePlan,
                Status = EvalStageStatus.Passed,
                ElapsedMs = stopwatch.ElapsedMilliseconds
            }, response);
        }
        catch (RpcException ex)
        {
            stopwatch.Stop();
            return (new EvalStageOutcome
            {
                Stage = EvalStageKind.ValidatePlan,
                Status = EvalStageStatus.Failed,
                Reason = $"grpc-{ex.StatusCode}".ToLowerInvariant(),
                Detail = ex.Status.Detail,
                ElapsedMs = stopwatch.ElapsedMilliseconds
            }, null);
        }
    }

    private async Task<(EvalStageOutcome Stage, Proto.DryRunPlanResponse? Response)> RunDryRunAsync(
        EvalScenario scenario,
        Proto.ProcessService.ProcessServiceClient client,
        AnalysisPlan domainPlan,
        Metadata headers,
        CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("eval.stage.dry_run");
        activity?.SetTag("eval.scenario.id", scenario.Id);
        activity?.SetTag("eval.protocol", "grpc");

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var request = new Proto.DryRunPlanRequest { Plan = ToProtoPlan(domainPlan) };
            var response = await client.DryRunPlanAsync(request, headers, cancellationToken: cancellationToken);
            stopwatch.Stop();

            var expected = scenario.ExpectedOutcome.EstimatedArtifactKinds;
            var actual = response.EstimatedArtifacts.Select(EvalProtoMap.ToDomainArtifactKind).ToArray();

            if (expected.Count > 0)
            {
                var missing = expected.Except(actual).ToArray();
                if (missing.Length > 0)
                {
                    return (new EvalStageOutcome
                    {
                        Stage = EvalStageKind.DryRun,
                        Status = EvalStageStatus.Failed,
                        Reason = "artifact-kinds-missing",
                        Detail = $"Expected artifact kinds [{string.Join(",", expected)}] were not all present in dry-run output [{string.Join(",", actual)}].",
                        ElapsedMs = stopwatch.ElapsedMilliseconds
                    }, response);
                }
            }

            return (new EvalStageOutcome
            {
                Stage = EvalStageKind.DryRun,
                Status = EvalStageStatus.Passed,
                ElapsedMs = stopwatch.ElapsedMilliseconds
            }, response);
        }
        catch (RpcException ex)
        {
            stopwatch.Stop();
            return (new EvalStageOutcome
            {
                Stage = EvalStageKind.DryRun,
                Status = EvalStageStatus.Failed,
                Reason = $"grpc-{ex.StatusCode}".ToLowerInvariant(),
                Detail = ex.Status.Detail,
                ElapsedMs = stopwatch.ElapsedMilliseconds
            }, null);
        }
    }

    private async Task<EvalProtocolParityOutcome> RunProtocolParityAsync(
        EvalScenario scenario,
        Proto.ValidatePlanResponse? grpcValidate,
        CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("eval.stage.protocol_parity");
        activity?.SetTag("eval.scenario.id", scenario.Id);

        var probes = new List<EvalProtocolProbe>();
        probes.Add(BuildGrpcValidateProbe(grpcValidate));

        var ogcProbe = await ProbeOgcProcessExecutionAsync(scenario, cancellationToken).ConfigureAwait(false);
        probes.Add(ogcProbe);

        var gpServerProbe = await ProbeGPServerSubmitJobAsync(cancellationToken).ConfigureAwait(false);
        probes.Add(gpServerProbe);

        var statuses = probes.Select(p => p.Status).ToArray();
        EvalStageStatus rolled;
        string? reason;
        if (statuses.Contains(EvalStageStatus.Failed))
        {
            rolled = EvalStageStatus.Failed;
            reason = string.Join("; ", probes.Where(p => p.Status == EvalStageStatus.Failed).Select(p => $"{p.Protocol}:{p.Assertion}:{p.Outcome}"));
        }
        else if (statuses.All(s => s == EvalStageStatus.Passed))
        {
            rolled = EvalStageStatus.Passed;
            reason = null;
        }
        else
        {
            rolled = EvalStageStatus.Skipped;
            reason = "one-or-more-probes-skipped";
        }

        return new EvalProtocolParityOutcome
        {
            Status = rolled,
            Probes = probes,
            Reason = reason
        };
    }

    private async Task<(EvalStageOutcome Stage, Proto.ExecutionJob? Job)> RunSubmitPlanJobAsync(
        EvalScenario scenario,
        Proto.ProcessService.ProcessServiceClient client,
        AnalysisPlan domainPlan,
        Metadata headers,
        CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("eval.stage.submit_plan_job");
        activity?.SetTag("eval.scenario.id", scenario.Id);
        activity?.SetTag("eval.protocol", "grpc");

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var request = new Proto.SubmitPlanJobRequest
            {
                Plan = ToProtoPlan(domainPlan),
                IdempotencyKey = $"eval-{scenario.Id}-{Guid.NewGuid():N}"
            };

            var job = await client.SubmitPlanJobAsync(request, headers, cancellationToken: cancellationToken);
            stopwatch.Stop();

            if (string.IsNullOrWhiteSpace(job.JobId))
            {
                return (new EvalStageOutcome
                {
                    Stage = EvalStageKind.SubmitPlanJob,
                    Status = EvalStageStatus.Failed,
                    Reason = "missing-job-id",
                    Detail = "SubmitPlanJob returned an empty JobId.",
                    ElapsedMs = stopwatch.ElapsedMilliseconds
                }, job);
            }

            if (job.Status is not Proto.JobStatus.Queued and not Proto.JobStatus.Provisioning)
            {
                return (new EvalStageOutcome
                {
                    Stage = EvalStageKind.SubmitPlanJob,
                    Status = EvalStageStatus.Failed,
                    Reason = "unexpected-initial-status",
                    Detail = $"Expected queued/provisioning initial status but got {job.Status}.",
                    ElapsedMs = stopwatch.ElapsedMilliseconds
                }, job);
            }

            return (new EvalStageOutcome
            {
                Stage = EvalStageKind.SubmitPlanJob,
                Status = EvalStageStatus.Passed,
                ElapsedMs = stopwatch.ElapsedMilliseconds
            }, job);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
        {
            stopwatch.Stop();
            return (new EvalStageOutcome
            {
                Stage = EvalStageKind.SubmitPlanJob,
                Status = EvalStageStatus.Skipped,
                Reason = "redis-unavailable",
                Detail = "Durable job store not configured; SubmitPlanJob requires Redis.",
                ElapsedMs = stopwatch.ElapsedMilliseconds
            }, null);
        }
        catch (RpcException ex)
        {
            stopwatch.Stop();
            return (new EvalStageOutcome
            {
                Stage = EvalStageKind.SubmitPlanJob,
                Status = EvalStageStatus.Failed,
                Reason = $"grpc-{ex.StatusCode}".ToLowerInvariant(),
                Detail = ex.Status.Detail,
                ElapsedMs = stopwatch.ElapsedMilliseconds
            }, null);
        }
    }

    // -----------------------------------------------------------------------
    // Protocol parity probes
    // -----------------------------------------------------------------------

    private static EvalProtocolProbe BuildGrpcValidateProbe(Proto.ValidatePlanResponse? response)
    {
        if (response == null)
        {
            return new EvalProtocolProbe
            {
                Protocol = Constants.Protocols.Grpc,
                Assertion = "plan-shape-accepted",
                Outcome = "grpc-failed",
                Status = EvalStageStatus.Failed
            };
        }

        return new EvalProtocolProbe
        {
            Protocol = Constants.Protocols.Grpc,
            Assertion = "plan-shape-accepted",
            Outcome = response.IsExecutable ? "executable" : $"rejected:{response.Violations.Count}-violations",
            Status = EvalStageStatus.Passed
        };
    }

    private async Task<EvalProtocolProbe> ProbeOgcProcessExecutionAsync(
        EvalScenario scenario,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                "/ogc/processes/processes/honua-geoprocessing/execution");
            request.Headers.Add("Prefer", "respond-async");
            var body = new EvalProbePayload
            {
                Inputs = new EvalProbePayloadInputs { Plan = scenario.PrecompiledPlan }
            };
            var payload = JsonSerializer.Serialize(body, EvalJsonContext.Default.EvalProbePayload);
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var response = await _fixture.Client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            // Accept 201 (queued), or 503 (jobStore unavailable in local dev).
            // The adapter's plan validator is our cross-check: if the canonical runtime
            // accepts the plan and the OGC validator rejects it, that's a parity failure.
            if (response.StatusCode == HttpStatusCode.Created)
            {
                return new EvalProtocolProbe
                {
                    Protocol = Constants.Protocols.OgcApiProcesses,
                    Assertion = "plan-shape-accepted",
                    Outcome = "created",
                    Status = EvalStageStatus.Passed
                };
            }

            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                return new EvalProtocolProbe
                {
                    Protocol = Constants.Protocols.OgcApiProcesses,
                    Assertion = "plan-shape-accepted",
                    Outcome = "service-unavailable",
                    Status = EvalStageStatus.Skipped
                };
            }

            if (response.StatusCode == HttpStatusCode.NotImplemented)
            {
                return new EvalProtocolProbe
                {
                    Protocol = Constants.Protocols.OgcApiProcesses,
                    Assertion = "plan-shape-accepted",
                    Outcome = "not-implemented",
                    Status = EvalStageStatus.Skipped
                };
            }

            return new EvalProtocolProbe
            {
                Protocol = Constants.Protocols.OgcApiProcesses,
                Assertion = "plan-shape-accepted",
                Outcome = $"unexpected-{(int)response.StatusCode}",
                Status = EvalStageStatus.Failed
            };
        }
        catch (HttpRequestException ex)
        {
            return new EvalProtocolProbe
            {
                Protocol = Constants.Protocols.OgcApiProcesses,
                Assertion = "plan-shape-accepted",
                Outcome = $"http-error:{ex.Message}",
                Status = EvalStageStatus.Failed
            };
        }
    }

    private async Task<EvalProtocolProbe> ProbeGPServerSubmitJobAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["f"] = "json",
                ["input_features"] = "eval-placeholder"
            });
            using var response = await _fixture.Client.PostAsync(
                "/rest/services/HonuaEval/GPServer/BufferAnalysis/submitJob",
                content,
                cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotImplemented)
            {
                return new EvalProtocolProbe
                {
                    Protocol = Constants.Protocols.GPServer,
                    Assertion = "submit-job-surface",
                    Outcome = "task-resolution-unavailable",
                    Status = EvalStageStatus.Skipped
                };
            }

            return new EvalProtocolProbe
            {
                Protocol = Constants.Protocols.GPServer,
                Assertion = "submit-job-surface",
                Outcome = $"status-{(int)response.StatusCode}",
                Status = EvalStageStatus.Failed
            };
        }
        catch (HttpRequestException ex)
        {
            return new EvalProtocolProbe
            {
                Protocol = Constants.Protocols.GPServer,
                Assertion = "service-info-reachable",
                Outcome = $"http-error:{ex.Message}",
                Status = EvalStageStatus.Failed
            };
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private GrpcChannel CreateGrpcChannel()
    {
        var handler = _fixture.CreateHandler();
        var grpcWebHandler = new GrpcWebHandler(GrpcWebMode.GrpcWeb, handler);
        return GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = grpcWebHandler
        });
    }

    private Metadata BuildGrpcHeaders()
    {
        var headers = new Metadata();
        if (!string.IsNullOrWhiteSpace(_fixture.CurrentSchema))
        {
            headers.Add("X-Honua-Test-Schema", _fixture.CurrentSchema);
        }
        return headers;
    }

    private static EvalOverallStatus RollupScenarioStatus(
        IReadOnlyList<EvalStageOutcome> stages,
        EvalProtocolParityOutcome parity)
    {
        var anyFailed = stages.Any(s => s.Status == EvalStageStatus.Failed) || parity.Status == EvalStageStatus.Failed;
        if (anyFailed)
        {
            return EvalOverallStatus.Failed;
        }

        var anySkipped = stages.Any(s => s.Status == EvalStageStatus.Skipped) || parity.Status == EvalStageStatus.Skipped;
        return anySkipped ? EvalOverallStatus.PassedWithSkips : EvalOverallStatus.Passed;
    }

    private static EvalStageOutcome BuildSkipOutcome(EvalStageKind stage, string reason, string detail)
        => new()
        {
            Stage = stage,
            Status = EvalStageStatus.Skipped,
            Reason = reason,
            Detail = detail,
            ElapsedMs = 0
        };

    private static EvalStageOutcome BuildProtocolParityStageOutcome(EvalProtocolParityOutcome parity, long elapsedMs)
    {
        var detail = parity.Probes.Count == 0
            ? null
            : string.Join("; ", parity.Probes.Select(p => $"{p.Protocol}:{p.Assertion}={p.Outcome}"));

        return new EvalStageOutcome
        {
            Stage = EvalStageKind.ProtocolParity,
            Status = parity.Status,
            Reason = parity.Reason,
            Detail = detail,
            ElapsedMs = elapsedMs
        };
    }

    private static EvalStageOutcome BuildModeScopedStage(
        EvalScenario scenario,
        EvalStageKind stage,
        bool inScope,
        string skipReason,
        string skipDetail)
    {
        if (!inScope)
        {
            return new EvalStageOutcome
            {
                Stage = stage,
                Status = EvalStageStatus.Skipped,
                Reason = "out-of-scope",
                Detail = $"Scenario '{scenario.Id}' ({scenario.Mode}) does not exercise {stage}.",
                ElapsedMs = 0
            };
        }

        return BuildSkipOutcome(stage, skipReason, skipDetail);
    }

    private static AnalysisIntent BuildDomainIntent(EvalIntentSpec spec)
    {
        return new AnalysisIntent
        {
            IntentId = spec.IntentId,
            Goal = spec.Goal,
            Mode = spec.Mode,
            RequestedOutputs = spec.RequestedOutputs,
            Constraints = spec.Constraints is null ? null : new IntentConstraints
            {
                AreaOfInterest = spec.Constraints.AreaOfInterest,
                SpatialReferenceId = spec.Constraints.SpatialReferenceId,
                TimeWindowStart = spec.Constraints.TimeWindowStart,
                TimeWindowEnd = spec.Constraints.TimeWindowEnd,
                Units = spec.Constraints.Units
            },
            Inputs = spec.Inputs,
            AssumptionPolicy = spec.AssumptionPolicy
        };
    }

    private static AnalysisPlan BuildDomainPlan(EvalPlanSpec spec)
    {
        return new AnalysisPlan
        {
            PlanId = spec.PlanId,
            IntentId = spec.IntentId,
            Steps = spec.Steps.Select(step => new AnalysisPlanStep
            {
                StepId = step.StepId,
                Kind = step.Kind,
                ProcessId = step.ProcessId,
                Inputs = step.Inputs,
                DependsOn = step.DependsOn
            }).ToArray(),
            Outputs = spec.Outputs
        };
    }

    private static Proto.AnalysisPlan ToProtoPlan(AnalysisPlan plan)
    {
        var proto = new Proto.AnalysisPlan
        {
            PlanId = plan.PlanId,
            IntentId = plan.IntentId
        };

        foreach (var step in plan.Steps)
        {
            var protoStep = new Proto.AnalysisPlanStep
            {
                StepId = step.StepId,
                Kind = EvalProtoMap.ToProtoPlanStepKind(step.Kind)
            };

            if (step.ProcessId != null)
            {
                protoStep.ProcessId = step.ProcessId;
            }

            foreach (var (key, value) in step.Inputs)
            {
                protoStep.Inputs[key] = value;
            }

            protoStep.DependsOn.AddRange(step.DependsOn);
            proto.Steps.Add(protoStep);
        }

        foreach (var output in plan.Outputs)
        {
            proto.Outputs.Add(EvalProtoMap.ToProtoArtifactKind(output));
        }

        return proto;
    }
}
