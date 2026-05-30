// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Grpc.Core;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Geoprocessing;
using Honua.Server.Features.ControlPlane;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Honua.TestKit.Helpers;
using NSubstitute;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Geoprocessing;

/// <summary>
/// Unit tests for the gRPC ProcessService implementation.
/// </summary>
[Protocol(TestProtocols.Grpc)]
public sealed class GrpcProcessServiceTests
{
    private readonly IExecutionJobStore _jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
    private readonly IUniversalProgressStore _progressStore = Substitute.For<IUniversalProgressStore>();
    private readonly IJobCancellationNotifier _cancellationNotifier = Substitute.For<IJobCancellationNotifier>();
    private readonly IOperatorAuthorizationEvaluator _authEvaluator = Substitute.For<IOperatorAuthorizationEvaluator>();
    private readonly IOperatorApprovalEvaluator _approvalEvaluator = Substitute.For<IOperatorApprovalEvaluator>();
    private readonly IGeoprocessingResultPackageStore _resultPackageStore = Substitute.For<IGeoprocessingResultPackageStore>();
    private readonly IProcessCatalog _processCatalog = new BuiltInProcessCatalog();
    private readonly HonuaProcessService _sut;

    public GrpcProcessServiceTests()
    {
        _authEvaluator
            .EvaluateAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AccessDecision.Allowed()));

        _approvalEvaluator
            .Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>())
            .Returns(ApprovalRequirement.NotRequired());

        var jobService = new GeoprocessingJobService(
            _progressStore, [_cancellationNotifier],
            _authEvaluator, _approvalEvaluator,
            _processCatalog,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GeoprocessingJobService>.Instance,
            _jobStore,
            resultPackageStore: _resultPackageStore);

        _sut = new HonuaProcessService(
            jobService,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<HonuaProcessService>.Instance);
    }

    // -----------------------------------------------------------------------
    // ValidatePlan
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public async Task ValidatePlan_WithValidPlan_ReturnsExecutable()
    {
        var request = new Proto.ValidatePlanRequest
        {
            Plan = CreateValidPlan()
        };

        var response = await _sut.ValidatePlan(request, CreateCallContext());

        response.Valid.Should().BeTrue();
        response.Issues.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public async Task ValidatePlan_WithEmptyPlanId_ReturnsNotExecutable()
    {
        var request = new Proto.ValidatePlanRequest
        {
            Plan = new Proto.ExecutionPlan
            {
                PlanId = "",
                SpecVersion = "intent-1",
                WorkflowFamily = Proto.WorkflowFamily.Analyze
            }
        };
        request.Plan.Steps.Add(CreateValidStep());

        var response = await _sut.ValidatePlan(request, CreateCallContext());

        response.Valid.Should().BeFalse();
        response.Issues.Should().ContainSingle(v => v.Message == "Plan identifier is required.");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public async Task ValidatePlan_WithNoSteps_ReturnsNotExecutable()
    {
        var request = new Proto.ValidatePlanRequest
        {
            Plan = new Proto.ExecutionPlan
            {
                PlanId = "plan-1",
                SpecVersion = "intent-1",
                WorkflowFamily = Proto.WorkflowFamily.Analyze
            }
        };

        var response = await _sut.ValidatePlan(request, CreateCallContext());

        response.Valid.Should().BeFalse();
        response.Issues.Should().ContainSingle(v => v.Message == "Plan must contain at least one step.");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public async Task ValidatePlan_WithNullPlan_ThrowsInvalidArgument()
    {
        var request = new Proto.ValidatePlanRequest();

        var act = async () => await _sut.ValidatePlan(request, CreateCallContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public async Task ValidatePlan_WhenApprovalRequired_StillReturnsCanonicalValidation()
    {
        _approvalEvaluator
            .Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>())
            .Returns(ApprovalRequirement.Required("policy-1", "destructive-action"));

        var request = new Proto.ValidatePlanRequest
        {
            Plan = CreateValidPlan()
        };

        var response = await _sut.ValidatePlan(request, CreateCallContext());

        response.Valid.Should().BeTrue();
        response.Issues.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // DryRunPlan
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/DryRunPlan")]
    public async Task DryRunPlan_WithValidPlan_ReturnsEstimation()
    {
        var plan = CreateValidPlan();
        plan.ExpectedOutputs.Add("feature_layer");

        var request = new Proto.DryRunPlanRequest { Plan = plan };

        var response = await _sut.DryRunPlan(request, CreateCallContext());

        response.Valid.Should().BeTrue();
        response.Result.EstimatedDurationSeconds.Should().Be(0);
        response.Result.EstimatedArtifacts.Should().ContainSingle(a => a.ArtifactClass == Proto.ArtifactClass.FeatureLayer);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/DryRunPlan")]
    public async Task DryRunPlan_WithUnknownProcessId_ReturnsValidationIssues()
    {
        var plan = CreatePlan();
        plan.Steps.Add(new Proto.PlanStep
        {
            StepId = "step-1",
            Kind = "geoprocess",
            Inputs = { ["processId"] = ToProtoParameterValue("does.not.exist") }
        });

        var request = new Proto.DryRunPlanRequest { Plan = plan };

        var response = await _sut.DryRunPlan(request, CreateCallContext());

        response.Valid.Should().BeFalse();
        response.Issues.Should().ContainSingle(issue => issue.Message.Contains("does.not.exist", StringComparison.Ordinal));
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/DryRunPlan")]
    public async Task DryRunPlan_WithMissingRequiredParameter_ReturnsValidationIssues()
    {
        var plan = CreatePlan();
        plan.Steps.Add(new Proto.PlanStep
        {
            StepId = "step-1",
            Kind = "geoprocess",
            Inputs = { ["processId"] = ToProtoParameterValue("geometry.buffer") }
        });

        var request = new Proto.DryRunPlanRequest { Plan = plan };

        var response = await _sut.DryRunPlan(request, CreateCallContext());

        response.Valid.Should().BeFalse();
        response.Issues.Should().Contain(issue => issue.Message.Contains("required", StringComparison.OrdinalIgnoreCase));
    }

    // -----------------------------------------------------------------------
    // ExecutePlan
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ExecutePlan")]
    public async Task ExecutePlan_ReturnsUnimplemented()
    {
        var request = new Proto.ExecutePlanRequest { Plan = CreateValidPlan() };

        var act = async () => await _sut.ExecutePlan(request, CreateCallContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.Unimplemented);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ExecutePlan")]
    public async Task ExecutePlan_Unauthorized_ThrowsPermissionDenied()
    {
        // Even though ExecutePlan is unimplemented (#721), auth is enforced first
        // so that unauthorized callers get 403 rather than an implementation-detail
        // 501 (consistent with the contract: auth on all mutating RPCs).
        _authEvaluator
            .EvaluateAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AccessDecision.Forbidden()));

        var request = new Proto.ExecutePlanRequest { Plan = CreateValidPlan() };

        var act = async () => await _sut.ExecutePlan(request, CreateCallContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }

    // -----------------------------------------------------------------------
    // SubmitJob
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /geospatial.v1.ProcessService/SubmitJob")]
    public async Task SubmitJob_WithValidPlan_CreatesJobAndReturns()
    {
        _jobStore.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var request = CreateSubmitJobRequest(CreateValidPlan(), "idem-key-1");

        var response = await _sut.SubmitJob(request, CreateCallContext());

        response.JobId.Should().NotBeNullOrWhiteSpace();
        response.State.Should().Be(Proto.JobState.Validated);
        await _jobStore.Received(1).TryCreateAsync(
            Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
        await _progressStore.Received(1).SetProgressAsync(
            Arg.Any<string>(), Arg.Any<Honua.Core.Features.Geoprocessing.Domain.GeoprocessingProgress>(),
            Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /geospatial.v1.ProcessService/SubmitJob")]
    public async Task SubmitJob_WhenRequestIsCancelled_ThrowsCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _jobStore.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromCanceled<bool>(cts.Token));

        var request = CreateSubmitJobRequest(CreateValidPlan(), "idem-cancelled");

        var act = async () => await _sut.SubmitJob(request, CreateCallContext(cts.Token));

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.Cancelled);
    }

    // -----------------------------------------------------------------------
    // GetJob
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/GetJob")]
    public async Task GetJob_WithExistingJob_ReturnsJob()
    {
        var jobRecord = CreateTestJobRecord("job-123", ExecutionJobStatus.Running);
        _jobStore.GetAsync("job-123", Arg.Any<CancellationToken>()).Returns(jobRecord);

        var request = new Proto.GetJobRequest { JobId = "job-123" };

        var response = await _sut.GetJob(request, CreateCallContext());

        response.JobId.Should().Be("job-123");
        response.State.Should().Be(Proto.JobState.Running);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/GetJob")]
    public async Task GetJob_WithMissingJob_ThrowsNotFound()
    {
        _jobStore.GetAsync("missing", Arg.Any<CancellationToken>()).Returns((ExecutionJobRecord?)null);

        var request = new Proto.GetJobRequest { JobId = "missing" };

        var act = async () => await _sut.GetJob(request, CreateCallContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/GetJob")]
    public async Task GetJob_WithEmptyJobId_ThrowsInvalidArgument()
    {
        var request = new Proto.GetJobRequest { JobId = "" };

        var act = async () => await _sut.GetJob(request, CreateCallContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    // -----------------------------------------------------------------------
    // GetJobResult
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/GetJobResult")]
    public async Task GetJobResult_WithNonTerminalJob_ThrowsFailedPrecondition()
    {
        var jobRecord = CreateTestJobRecord("job-123", ExecutionJobStatus.Running);
        _jobStore.GetAsync("job-123", Arg.Any<CancellationToken>()).Returns(jobRecord);

        var request = new Proto.GetJobResultRequest { JobId = "job-123" };

        var act = async () => await _sut.GetJobResult(request, CreateCallContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/GetJobResult")]
    public async Task GetJobResult_WithTerminalJob_ReturnsResultPackage()
    {
        var jobRecord = CreateTestJobRecord("job-123", ExecutionJobStatus.Succeeded) with
        {
            Version = 5,
            CompletedAt = DateTimeOffset.UtcNow,
            ArtifactReferences = ["https://example.test/artifacts/output.geojson"],
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "test-workload",
                Parameters = new Dictionary<string, string>
                {
                    [ExecutionJobParameterKeys.GeoprocessingPlanId] = "plan-1",
                    [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = "geometry.buffer",
                    [ExecutionJobParameterKeys.GeoprocessingOutputArtifactKinds] = "FeatureLayer"
                }
            }
        };
        _jobStore.GetAsync("job-123", Arg.Any<CancellationToken>()).Returns(jobRecord);
        _resultPackageStore.GetAsync("job-123", Arg.Any<CancellationToken>())
            .Returns((AnalysisResultPackage?)null);

        var request = new Proto.GetJobResultRequest { JobId = "job-123" };

        var response = await _sut.GetJobResult(request, CreateCallContext());

        response.Result.ResultId.Should().Be("job-123:v5");
        response.Result.Status.Should().Be(Proto.JobState.Completed);
        response.Result.Artifacts.Should().ContainSingle();
    }

    // -----------------------------------------------------------------------
    // CancelJob
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Delete)]
    [Endpoint("POST /geospatial.v1.ProcessService/CancelJob")]
    public async Task CancelJob_WhenWorkerOwnsJob_DelegatesWithoutPersisting()
    {
        var jobRecord = CreateTestJobRecord("job-123", ExecutionJobStatus.Running);
        _jobStore.GetAsync("job-123", Arg.Any<CancellationToken>()).Returns(jobRecord);
        _cancellationNotifier.Cancel("job-123").Returns(true);

        var request = new Proto.CancelJobRequest { JobId = "job-123" };

        var response = await _sut.CancelJob(request, CreateCallContext());

        response.Should().NotBeNull();
        _cancellationNotifier.Received(1).Cancel("job-123");
        await _jobStore.DidNotReceive().SetAsync(
            Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Delete)]
    [Endpoint("POST /geospatial.v1.ProcessService/CancelJob")]
    public async Task CancelJob_WhenNoWorkerClaims_PersistsCancelled()
    {
        var jobRecord = CreateTestJobRecord("job-123", ExecutionJobStatus.Running);
        _jobStore.GetAsync("job-123", Arg.Any<CancellationToken>()).Returns(jobRecord);
        _cancellationNotifier.Cancel("job-123").Returns(false);

        var request = new Proto.CancelJobRequest { JobId = "job-123" };

        var response = await _sut.CancelJob(request, CreateCallContext());

        response.Should().NotBeNull();
        _cancellationNotifier.Received(1).Cancel("job-123");
        await _jobStore.Received(1).TrySetAsync(
            Arg.Is<ExecutionJobRecord>(j => j.Status == ExecutionJobStatus.Cancelled),
            Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Delete)]
    [Endpoint("POST /geospatial.v1.ProcessService/CancelJob")]
    public async Task CancelJob_WithMissingJob_ThrowsNotFound()
    {
        _jobStore.GetAsync("missing", Arg.Any<CancellationToken>()).Returns((ExecutionJobRecord?)null);

        var request = new Proto.CancelJobRequest { JobId = "missing" };

        var act = async () => await _sut.CancelJob(request, CreateCallContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [UnitTest]
    [Operation(Operations.Delete)]
    [Endpoint("POST /geospatial.v1.ProcessService/CancelJob")]
    public async Task CancelJob_ApprovalRequired_ThrowsFailedPrecondition()
    {
        var jobRecord = CreateTestJobRecord("job-123", ExecutionJobStatus.Running);
        _jobStore.GetAsync("job-123", Arg.Any<CancellationToken>()).Returns(jobRecord);

        _approvalEvaluator
            .Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Is<OperatorAuthorizationRequest>(r => r.IsDestructive))
            .Returns(ApprovalRequirement.Required("destructive-policy", "destructive-action"));

        var request = new Proto.CancelJobRequest { JobId = "job-123" };

        var act = async () => await _sut.CancelJob(request, CreateCallContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public async Task ValidatePlan_WithUnspecifiedStepKind_ThrowsInvalidArgument()
    {
        var request = new Proto.ValidatePlanRequest
        {
            Plan = new Proto.ExecutionPlan
            {
                PlanId = "plan-1",
                SpecVersion = "intent-1",
                WorkflowFamily = Proto.WorkflowFamily.Analyze
            }
        };
        request.Plan.Steps.Add(new Proto.PlanStep
        {
            StepId = "step-bad",
            Kind = ""
        });

        var act = async () => await _sut.ValidatePlan(request, CreateCallContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/DryRunPlan")]
    public async Task DryRunPlan_WithUnspecifiedArtifactKind_ThrowsInvalidArgument()
    {
        var plan = CreateValidPlan();
        plan.ExpectedOutputs.Add("");

        var request = new Proto.DryRunPlanRequest { Plan = plan };

        var act = async () => await _sut.DryRunPlan(request, CreateCallContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    // -----------------------------------------------------------------------
    // CancelJob – terminal state handling
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Delete)]
    [Endpoint("POST /geospatial.v1.ProcessService/CancelJob")]
    public async Task CancelJob_WithAlreadyCancelled_ReturnsIdempotentSuccess()
    {
        var jobRecord = CreateTestJobRecord("job-123", ExecutionJobStatus.Cancelled);
        _jobStore.GetAsync("job-123", Arg.Any<CancellationToken>()).Returns(jobRecord);

        var request = new Proto.CancelJobRequest { JobId = "job-123" };

        var response = await _sut.CancelJob(request, CreateCallContext());

        response.Should().NotBeNull();
        _cancellationNotifier.DidNotReceive().Cancel(Arg.Any<string>());
        await _jobStore.DidNotReceive().SetAsync(
            Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Delete)]
    [Endpoint("POST /geospatial.v1.ProcessService/CancelJob")]
    public async Task CancelJob_WithSucceededJob_ThrowsFailedPrecondition()
    {
        var jobRecord = CreateTestJobRecord("job-123", ExecutionJobStatus.Succeeded);
        _jobStore.GetAsync("job-123", Arg.Any<CancellationToken>()).Returns(jobRecord);

        var request = new Proto.CancelJobRequest { JobId = "job-123" };

        var act = async () => await _sut.CancelJob(request, CreateCallContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);
    }

    // -----------------------------------------------------------------------
    // SubmitJob – idempotency
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /geospatial.v1.ProcessService/SubmitJob")]
    public async Task SubmitJob_StoresProgressInQueuedState()
    {
        _jobStore.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var request = CreateSubmitJobRequest(CreateValidPlan(), "idem-progress");

        await _sut.SubmitJob(request, CreateCallContext());

        await _progressStore.Received(1).SetProgressAsync(
            Arg.Any<string>(),
            Arg.Is<Honua.Core.Features.Geoprocessing.Domain.GeoprocessingProgress>(p =>
                p.WorkflowStatus == Honua.Core.Features.Geoprocessing.Domain.GeoprocessingWorkflowStatus.AwaitingExecution &&
                p.CurrentStage == Honua.Core.Features.Geoprocessing.Domain.GeoprocessingStageKind.Execute &&
                p.CurrentPhase == "Queued" &&
                p.PlanId == "plan-1"),
            Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /geospatial.v1.ProcessService/SubmitJob")]
    public async Task SubmitJob_WithDuplicateIdempotencyKey_ReturnsExistingJob()
    {
        _jobStore.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var plan = CreateValidPlan();
        var request = CreateSubmitJobRequest(plan, "idem-key-dup");

        var existingRecord = CreateTestJobRecord("placeholder", ExecutionJobStatus.Queued);
        existingRecord = existingRecord with
        {
            Audit = new Honua.Core.Features.ControlPlane.Domain.OperationAuditInfo
            {
                IdempotencyKey = "idem-key-dup",
                RequestFingerprint = ComputeExpectedFingerprint(plan)
            }
        };

        _jobStore.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(existingRecord);

        var response = await _sut.SubmitJob(request, CreateCallContext());

        response.Should().NotBeNull();
        await _progressStore.DidNotReceive().SetProgressAsync(
            Arg.Any<string>(), Arg.Any<Honua.Core.Features.Geoprocessing.Domain.GeoprocessingProgress>(),
            Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /geospatial.v1.ProcessService/SubmitJob")]
    public async Task SubmitJob_WithMismatchedIdempotencyKey_ThrowsAlreadyExists()
    {
        _jobStore.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var existingRecord = CreateTestJobRecord("placeholder", ExecutionJobStatus.Queued);
        existingRecord = existingRecord with
        {
            Audit = new Honua.Core.Features.ControlPlane.Domain.OperationAuditInfo
            {
                IdempotencyKey = "idem-key-dup",
                RequestFingerprint = "different-fingerprint"
            }
        };

        _jobStore.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(existingRecord);

        var request = CreateSubmitJobRequest(CreateValidPlan(), "idem-key-dup");

        var act = async () => await _sut.SubmitJob(request, CreateCallContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.AlreadyExists);
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /geospatial.v1.ProcessService/SubmitJob")]
    public async Task SubmitJob_WithEmptyPlanId_ThrowsInvalidArgument()
    {
        var plan = new Proto.ExecutionPlan
        {
            PlanId = "",
            SpecVersion = "intent-1",
            WorkflowFamily = Proto.WorkflowFamily.Analyze
        };
        plan.Steps.Add(CreateValidStep());

        var request = CreateSubmitJobRequest(plan, "idem-1");

        var act = async () => await _sut.SubmitJob(request, CreateCallContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("Plan identifier");
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /geospatial.v1.ProcessService/SubmitJob")]
    public async Task SubmitJob_WithNoSteps_ThrowsInvalidArgument()
    {
        var plan = new Proto.ExecutionPlan
        {
            PlanId = "plan-1",
            SpecVersion = "intent-1",
            WorkflowFamily = Proto.WorkflowFamily.Analyze
        };

        var request = CreateSubmitJobRequest(plan, "idem-1");

        var act = async () => await _sut.SubmitJob(request, CreateCallContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("at least one step");
    }

    // -----------------------------------------------------------------------
    // Fingerprint canonicalization
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /geospatial.v1.ProcessService/SubmitJob")]
    public void Fingerprint_AmbiguousDelimiterValues_ProduceDifferentHashes()
    {
        var planA = CreatePlan("p", "i");
        var stepA = new Proto.PlanStep
        {
            StepId = "s1",
            Kind = "geoprocess"
        };
        SetProcessId(stepA, "buffer");
        stepA.Inputs["k"] = ToProtoParameterValue("a>b");
        planA.Steps.Add(stepA);

        var planB = CreatePlan("p", "i");
        var stepB = new Proto.PlanStep
        {
            StepId = "s1",
            Kind = "geoprocess"
        };
        SetProcessId(stepB, "buffer");
        stepB.Inputs["k"] = ToProtoParameterValue("a");
        stepB.Dependencies.Add("b");
        planB.Steps.Add(stepB);

        var fpA = ComputeExpectedFingerprint(planA);
        var fpB = ComputeExpectedFingerprint(planB);

        fpA.Should().NotBe(fpB, "plans with different inputs vs dependencies must hash differently");
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /geospatial.v1.ProcessService/SubmitJob")]
    public void Fingerprint_ReorderedDependsOn_ProducesSameHash()
    {
        var planA = CreatePlan("p", "i");
        var stepA = new Proto.PlanStep
        {
            StepId = "s1",
            Kind = "geoprocess"
        };
        SetProcessId(stepA, "buffer");
        stepA.Dependencies.Add("dep-a");
        stepA.Dependencies.Add("dep-b");
        planA.Steps.Add(stepA);

        var planB = CreatePlan("p", "i");
        var stepB = new Proto.PlanStep
        {
            StepId = "s1",
            Kind = "geoprocess"
        };
        SetProcessId(stepB, "buffer");
        stepB.Dependencies.Add("dep-b");
        stepB.Dependencies.Add("dep-a");
        planB.Steps.Add(stepB);

        var fpA = ComputeExpectedFingerprint(planA);
        var fpB = ComputeExpectedFingerprint(planB);

        fpA.Should().Be(fpB, "semantically equivalent plans with reordered dependencies must hash identically");
    }

    // -----------------------------------------------------------------------
    // Authorization
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public async Task ValidatePlan_Unauthenticated_ThrowsUnauthenticated()
    {
        _authEvaluator
            .EvaluateAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AccessDecision.RequiresAuth()));

        var request = new Proto.ValidatePlanRequest { Plan = CreateValidPlan() };

        var act = async () => await _sut.ValidatePlan(request, CreateCallContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.Unauthenticated);
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /geospatial.v1.ProcessService/SubmitJob")]
    public async Task SubmitJob_WhenApprovalRequired_ThrowsFailedPrecondition()
    {
        _approvalEvaluator
            .Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>())
            .Returns(ApprovalRequirement.Required("policy-1", "destructive-action"));

        var request = CreateSubmitJobRequest(CreateValidPlan(), "idem-key-1");

        var act = async () => await _sut.SubmitJob(request, CreateCallContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);
        ex.Which.Status.Detail.Should().Contain("approval");
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /geospatial.v1.ProcessService/SubmitJob")]
    public async Task SubmitJob_Unauthorized_ThrowsPermissionDenied()
    {
        _authEvaluator
            .EvaluateAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AccessDecision.Forbidden()));

        var request = new Proto.SubmitJobRequest { Plan = CreateValidPlan() };

        var act = async () => await _sut.SubmitJob(request, CreateCallContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }

    // -----------------------------------------------------------------------
    // Auth-before-validation ordering
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public async Task ValidatePlan_UnauthenticatedWithInvalidPlan_ThrowsUnauthenticatedNotInvalidArgument()
    {
        _authEvaluator
            .EvaluateAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AccessDecision.RequiresAuth()));

        var request = new Proto.ValidatePlanRequest
        {
            Plan = new Proto.ExecutionPlan
            {
                PlanId = "plan-1",
                SpecVersion = "intent-1",
                WorkflowFamily = Proto.WorkflowFamily.Analyze
            }
        };
        request.Plan.Steps.Add(new Proto.PlanStep
        {
            StepId = "step-bad",
            Kind = ""
        });

        var act = async () => await _sut.ValidatePlan(request, CreateCallContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.Unauthenticated,
            "auth must be checked before proto structural validation");
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /geospatial.v1.ProcessService/SubmitJob")]
    public async Task SubmitJob_UnauthenticatedWithInvalidPlan_ThrowsUnauthenticatedNotInvalidArgument()
    {
        _authEvaluator
            .EvaluateAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AccessDecision.RequiresAuth()));

        var request = new Proto.SubmitJobRequest
        {
            Plan = new Proto.ExecutionPlan
            {
                PlanId = "plan-1",
                SpecVersion = "intent-1",
                WorkflowFamily = Proto.WorkflowFamily.Analyze
            }
        };
        request.Plan.Steps.Add(new Proto.PlanStep
        {
            StepId = "step-bad",
            Kind = ""
        });

        var act = async () => await _sut.SubmitJob(request, CreateCallContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.Unauthenticated,
            "auth must be checked before proto structural validation");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static Proto.ExecutionPlan CreateValidPlan()
    {
        var plan = CreatePlan();
        plan.Steps.Add(CreateValidStep());
        return plan;
    }

    private static Proto.ExecutionPlan CreatePlan(string planId = "plan-1", string specVersion = "intent-1")
        => new()
        {
            PlanId = planId,
            SpecVersion = specVersion,
            WorkflowFamily = Proto.WorkflowFamily.Analyze
        };

    private static Proto.PlanStep CreateValidStep()
    {
        var step = new Proto.PlanStep
        {
            StepId = "step-1",
            Kind = "geoprocess"
        };
        SetProcessId(step, "geometry.buffer");
        step.Inputs["wkb"] = ToProtoParameterValue("AAAA");
        step.Inputs["srid"] = ToProtoParameterValue("4326");
        step.Inputs["distance"] = ToProtoParameterValue("100");
        return step;
    }

    private static Proto.SubmitJobRequest CreateSubmitJobRequest(Proto.ExecutionPlan plan, string idempotencyKey)
    {
        var request = new Proto.SubmitJobRequest
        {
            Plan = plan,
            Context = new Proto.ExecutionContext()
        };
        request.Context.Metadata["idempotency_key"] = idempotencyKey;
        return request;
    }

    private static void SetProcessId(Proto.PlanStep step, string processId)
        => step.Inputs["processId"] = ToProtoParameterValue(processId);

    private static Proto.ParameterValue ToProtoParameterValue(string value)
        => new() { StringValue = value };

    private static string ComputeExpectedFingerprint(Proto.ExecutionPlan protoPlan)
    {
        var domainPlan = GeoprocessingConversionHelpers.ToDomainPlan(protoPlan);

        using var buffer = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("planId", domainPlan.PlanId);
            writer.WriteString("intentId", domainPlan.IntentId);

            writer.WriteStartArray("steps");
            foreach (var step in domainPlan.Steps)
            {
                writer.WriteStartObject();
                writer.WriteString("stepId", step.StepId);
                writer.WriteString("kind", step.Kind.ToString());
                writer.WriteString("processId", step.ProcessId ?? "");

                writer.WriteStartArray("inputs");
                foreach (var kv in step.Inputs.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    writer.WriteStartObject();
                    writer.WriteString("Key", kv.Key);
                    writer.WriteString("Value", kv.Value);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();

                writer.WriteStartArray("dependsOn");
                foreach (var d in step.DependsOn.OrderBy(d => d, StringComparer.Ordinal))
                {
                    writer.WriteStringValue(d);
                }
                writer.WriteEndArray();

                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("outputs");
            foreach (var o in domainPlan.Outputs.Select(o => o.ToString()).OrderBy(o => o, StringComparer.Ordinal))
            {
                writer.WriteStringValue(o);
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
    }

    private static ExecutionJobRecord CreateTestJobRecord(string jobId, ExecutionJobStatus status)
    {
        var now = DateTimeOffset.UtcNow;
        return new ExecutionJobRecord
        {
            OperationId = jobId,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "test-workload"
            }
        };
    }

    private static TestServerCallContext CreateCallContext(CancellationToken cancellationToken = default)
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "test-user")], "Test"))
        };

        var ctx = new TestServerCallContext(cancellationToken);
        ctx.UserState["__HttpContext"] = httpContext;
        return ctx;
    }

    private sealed class TestServerCallContext : ServerCallContext, IDisposable
    {
        private readonly CancellationTokenSource? _cts;
        private readonly CancellationToken _cancellationToken;
        private readonly Metadata _responseTrailers = new();

        public TestServerCallContext(CancellationToken cancellationToken)
        {
            if (cancellationToken.CanBeCanceled)
            {
                _cancellationToken = cancellationToken;
                return;
            }

            _cts = new CancellationTokenSource();
            _cancellationToken = _cts.Token;
        }

        public void Dispose() => _cts?.Dispose();

        protected override string MethodCore => "/geospatial.v1.ProcessService/ValidatePlan";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "127.0.0.1";
        protected override DateTime DeadlineCore => DateTime.UtcNow.AddMinutes(5);
        protected override Metadata RequestHeadersCore => new();
        protected override CancellationToken CancellationTokenCore => _cancellationToken;
        protected override Metadata ResponseTrailersCore => _responseTrailers;
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => new(null, new Dictionary<string, List<AuthProperty>>());

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
            throw new NotImplementedException();

        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) =>
            Task.CompletedTask;
    }
}
