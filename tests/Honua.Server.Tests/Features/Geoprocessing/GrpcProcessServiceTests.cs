// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Grpc.Core;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Server.Features.Geoprocessing;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Geoprocessing;

/// <summary>
/// Unit tests for the gRPC ProcessService implementation.
/// </summary>
[Protocol(Protocols.Grpc)]
public sealed class GrpcProcessServiceTests
{
    private readonly IExecutionJobStore _jobStore = Substitute.For<IExecutionJobStore>();
    private readonly IUniversalProgressStore _progressStore = Substitute.For<IUniversalProgressStore>();
    private readonly IJobCancellationNotifier _cancellationNotifier = Substitute.For<IJobCancellationNotifier>();
    private readonly IOperatorAuthorizationEvaluator _authEvaluator = Substitute.For<IOperatorAuthorizationEvaluator>();
    private readonly IOperatorApprovalEvaluator _approvalEvaluator = Substitute.For<IOperatorApprovalEvaluator>();
    private readonly HonuaProcessService _sut;

    public GrpcProcessServiceTests()
    {
        _authEvaluator
            .EvaluateAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(AccessDecision.Allowed());

        _approvalEvaluator
            .Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>())
            .Returns(ApprovalRequirement.NotRequired());

        _sut = new HonuaProcessService(
            _progressStore, _cancellationNotifier,
            _authEvaluator, _approvalEvaluator,
            Substitute.For<ILogger<HonuaProcessService>>(),
            _jobStore);
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

        response.IsExecutable.Should().BeTrue();
        response.Violations.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public async Task ValidatePlan_WithEmptyPlanId_ReturnsNotExecutable()
    {
        var request = new Proto.ValidatePlanRequest
        {
            Plan = new Proto.AnalysisPlan
            {
                PlanId = "",
                IntentId = "intent-1"
            }
        };
        request.Plan.Steps.Add(CreateValidStep());

        var response = await _sut.ValidatePlan(request, CreateCallContext());

        response.IsExecutable.Should().BeFalse();
        response.Violations.Should().ContainSingle(v => v.Code == "EMPTY_PLAN_ID");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public async Task ValidatePlan_WithNoSteps_ReturnsNotExecutable()
    {
        var request = new Proto.ValidatePlanRequest
        {
            Plan = new Proto.AnalysisPlan
            {
                PlanId = "plan-1",
                IntentId = "intent-1"
            }
        };

        var response = await _sut.ValidatePlan(request, CreateCallContext());

        response.IsExecutable.Should().BeFalse();
        response.Violations.Should().ContainSingle(v => v.Code == "EMPTY_STEPS");
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
    public async Task ValidatePlan_WhenApprovalRequired_SetsRequiresApproval()
    {
        _approvalEvaluator
            .Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>())
            .Returns(ApprovalRequirement.Required("policy-1", "destructive-action"));

        var request = new Proto.ValidatePlanRequest
        {
            Plan = CreateValidPlan()
        };

        var response = await _sut.ValidatePlan(request, CreateCallContext());

        response.RequiresApproval.Should().BeTrue();
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
        plan.Outputs.Add(Proto.ArtifactKind.FeatureLayer);

        var request = new Proto.DryRunPlanRequest { Plan = plan };

        var response = await _sut.DryRunPlan(request, CreateCallContext());

        response.EstimatedDurationSeconds.Should().Be(0);
        response.EstimatedArtifacts.Should().ContainSingle(a => a == Proto.ArtifactKind.FeatureLayer);
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
        _authEvaluator
            .Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>())
            .Returns(AccessDecision.Forbidden());

        var request = new Proto.ExecutePlanRequest { Plan = CreateValidPlan() };

        var act = async () => await _sut.ExecutePlan(request, CreateCallContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }

    // -----------------------------------------------------------------------
    // SubmitPlanJob
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /geospatial.v1.ProcessService/SubmitPlanJob")]
    public async Task SubmitPlanJob_WithValidPlan_CreatesJobAndReturns()
    {
        _jobStore.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var request = new Proto.SubmitPlanJobRequest
        {
            Plan = CreateValidPlan(),
            IdempotencyKey = "idem-key-1"
        };

        var response = await _sut.SubmitPlanJob(request, CreateCallContext());

        response.JobId.Should().NotBeNullOrWhiteSpace();
        response.Status.Should().Be(Proto.JobStatus.Queued);
        await _jobStore.Received(1).TryCreateAsync(
            Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
        await _progressStore.Received(1).SetProgressAsync(
            Arg.Any<string>(), Arg.Any<Honua.Core.Features.Geoprocessing.Domain.GeoprocessingProgress>(),
            Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
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
        response.Status.Should().Be(Proto.JobStatus.Running);
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
    // GetJobResults
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/GetJobResults")]
    public async Task GetJobResults_WithNonTerminalJob_ThrowsFailedPrecondition()
    {
        var jobRecord = CreateTestJobRecord("job-123", ExecutionJobStatus.Running);
        _jobStore.GetAsync("job-123", Arg.Any<CancellationToken>()).Returns(jobRecord);

        var request = new Proto.GetJobResultsRequest { JobId = "job-123" };

        var act = async () => await _sut.GetJobResults(request, CreateCallContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/GetJobResults")]
    public async Task GetJobResults_WithTerminalJob_ThrowsNotFound()
    {
        var jobRecord = CreateTestJobRecord("job-123", ExecutionJobStatus.Succeeded);
        _jobStore.GetAsync("job-123", Arg.Any<CancellationToken>()).Returns(jobRecord);

        var request = new Proto.GetJobResultsRequest { JobId = "job-123" };

        var act = async () => await _sut.GetJobResults(request, CreateCallContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.NotFound);
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
        await _jobStore.Received(1).SetAsync(
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
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public async Task ValidatePlan_WithUnspecifiedStepKind_ThrowsInvalidArgument()
    {
        var request = new Proto.ValidatePlanRequest
        {
            Plan = new Proto.AnalysisPlan
            {
                PlanId = "plan-1",
                IntentId = "intent-1"
            }
        };
        request.Plan.Steps.Add(new Proto.AnalysisPlanStep
        {
            StepId = "step-bad",
            Kind = Proto.PlanStepKind.Unspecified
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
        plan.Outputs.Add(Proto.ArtifactKind.Unspecified);

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
    // SubmitPlanJob – idempotency
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /geospatial.v1.ProcessService/SubmitPlanJob")]
    public async Task SubmitPlanJob_StoresProgressInQueuedState()
    {
        _jobStore.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var request = new Proto.SubmitPlanJobRequest
        {
            Plan = CreateValidPlan(),
            IdempotencyKey = "idem-progress"
        };

        await _sut.SubmitPlanJob(request, CreateCallContext());

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
    [Endpoint("POST /geospatial.v1.ProcessService/SubmitPlanJob")]
    public async Task SubmitPlanJob_WithDuplicateIdempotencyKey_ReturnsExistingJob()
    {
        _jobStore.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var plan = CreateValidPlan();
        var request = new Proto.SubmitPlanJobRequest
        {
            Plan = plan,
            IdempotencyKey = "idem-key-dup"
        };

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

        var response = await _sut.SubmitPlanJob(request, CreateCallContext());

        response.Should().NotBeNull();
        await _progressStore.DidNotReceive().SetProgressAsync(
            Arg.Any<string>(), Arg.Any<Honua.Core.Features.Geoprocessing.Domain.GeoprocessingProgress>(),
            Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /geospatial.v1.ProcessService/SubmitPlanJob")]
    public async Task SubmitPlanJob_WithMismatchedIdempotencyKey_ThrowsAlreadyExists()
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

        var request = new Proto.SubmitPlanJobRequest
        {
            Plan = CreateValidPlan(),
            IdempotencyKey = "idem-key-dup"
        };

        var act = async () => await _sut.SubmitPlanJob(request, CreateCallContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.AlreadyExists);
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /geospatial.v1.ProcessService/SubmitPlanJob")]
    public async Task SubmitPlanJob_WithEmptyPlanId_ThrowsInvalidArgument()
    {
        var plan = new Proto.AnalysisPlan
        {
            PlanId = "",
            IntentId = "intent-1"
        };
        plan.Steps.Add(CreateValidStep());

        var request = new Proto.SubmitPlanJobRequest { Plan = plan, IdempotencyKey = "idem-1" };

        var act = async () => await _sut.SubmitPlanJob(request, CreateCallContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("Plan identifier");
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /geospatial.v1.ProcessService/SubmitPlanJob")]
    public async Task SubmitPlanJob_WithNoSteps_ThrowsInvalidArgument()
    {
        var plan = new Proto.AnalysisPlan
        {
            PlanId = "plan-1",
            IntentId = "intent-1"
        };

        var request = new Proto.SubmitPlanJobRequest { Plan = plan, IdempotencyKey = "idem-1" };

        var act = async () => await _sut.SubmitPlanJob(request, CreateCallContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("at least one step");
    }

    // -----------------------------------------------------------------------
    // Fingerprint canonicalization
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /geospatial.v1.ProcessService/SubmitPlanJob")]
    public void Fingerprint_AmbiguousDelimiterValues_ProduceDifferentHashes()
    {
        var planA = new Proto.AnalysisPlan { PlanId = "p", IntentId = "i" };
        var stepA = new Proto.AnalysisPlanStep
        {
            StepId = "s1",
            Kind = Proto.PlanStepKind.Geoprocess,
            ProcessId = "buffer"
        };
        stepA.Inputs.Add("k", "a>b");
        planA.Steps.Add(stepA);

        var planB = new Proto.AnalysisPlan { PlanId = "p", IntentId = "i" };
        var stepB = new Proto.AnalysisPlanStep
        {
            StepId = "s1",
            Kind = Proto.PlanStepKind.Geoprocess,
            ProcessId = "buffer"
        };
        stepB.Inputs.Add("k", "a");
        stepB.DependsOn.Add("b");
        planB.Steps.Add(stepB);

        var fpA = ComputeExpectedFingerprint(planA);
        var fpB = ComputeExpectedFingerprint(planB);

        fpA.Should().NotBe(fpB, "plans with different inputs vs dependencies must hash differently");
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /geospatial.v1.ProcessService/SubmitPlanJob")]
    public void Fingerprint_ReorderedDependsOn_ProducesSameHash()
    {
        var planA = new Proto.AnalysisPlan { PlanId = "p", IntentId = "i" };
        var stepA = new Proto.AnalysisPlanStep
        {
            StepId = "s1",
            Kind = Proto.PlanStepKind.Geoprocess,
            ProcessId = "buffer"
        };
        stepA.DependsOn.Add("dep-a");
        stepA.DependsOn.Add("dep-b");
        planA.Steps.Add(stepA);

        var planB = new Proto.AnalysisPlan { PlanId = "p", IntentId = "i" };
        var stepB = new Proto.AnalysisPlanStep
        {
            StepId = "s1",
            Kind = Proto.PlanStepKind.Geoprocess,
            ProcessId = "buffer"
        };
        stepB.DependsOn.Add("dep-b");
        stepB.DependsOn.Add("dep-a");
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
            .Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>())
            .Returns(AccessDecision.RequiresAuth());

        var request = new Proto.ValidatePlanRequest { Plan = CreateValidPlan() };

        var act = async () => await _sut.ValidatePlan(request, CreateCallContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.Unauthenticated);
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /geospatial.v1.ProcessService/SubmitPlanJob")]
    public async Task SubmitPlanJob_WhenApprovalRequired_ThrowsFailedPrecondition()
    {
        _approvalEvaluator
            .Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>())
            .Returns(ApprovalRequirement.Required("policy-1", "destructive-action"));

        var request = new Proto.SubmitPlanJobRequest
        {
            Plan = CreateValidPlan(),
            IdempotencyKey = "idem-key-1"
        };

        var act = async () => await _sut.SubmitPlanJob(request, CreateCallContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);
        ex.Which.Status.Detail.Should().Contain("approval");
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /geospatial.v1.ProcessService/SubmitPlanJob")]
    public async Task SubmitPlanJob_Unauthorized_ThrowsPermissionDenied()
    {
        _authEvaluator
            .Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>())
            .Returns(AccessDecision.Forbidden());

        var request = new Proto.SubmitPlanJobRequest { Plan = CreateValidPlan() };

        var act = async () => await _sut.SubmitPlanJob(request, CreateCallContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static Proto.AnalysisPlan CreateValidPlan()
    {
        var plan = new Proto.AnalysisPlan
        {
            PlanId = "plan-1",
            IntentId = "intent-1"
        };
        plan.Steps.Add(CreateValidStep());
        return plan;
    }

    private static Proto.AnalysisPlanStep CreateValidStep()
        => new()
        {
            StepId = "step-1",
            Kind = Proto.PlanStepKind.Geoprocess,
            ProcessId = "buffer"
        };

    private static string ComputeExpectedFingerprint(Proto.AnalysisPlan protoPlan)
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

    private static TestServerCallContext CreateCallContext()
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "test-user")], "Test"))
        };

        var ctx = new TestServerCallContext();
        ctx.UserState["__HttpContext"] = httpContext;
        return ctx;
    }

    private sealed class TestServerCallContext : ServerCallContext, IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Metadata _responseTrailers = new();

        public void Dispose() => _cts.Dispose();

        protected override string MethodCore => "/geospatial.v1.ProcessService/ValidatePlan";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "127.0.0.1";
        protected override DateTime DeadlineCore => DateTime.UtcNow.AddMinutes(5);
        protected override Metadata RequestHeadersCore => new();
        protected override CancellationToken CancellationTokenCore => _cts.Token;
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
