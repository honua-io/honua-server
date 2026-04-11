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
            .Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>())
            .Returns(AccessDecision.Allowed());

        _approvalEvaluator
            .Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>())
            .Returns(ApprovalRequirement.NotRequired());

        _sut = new HonuaProcessService(
            _jobStore, _progressStore, _cancellationNotifier,
            _authEvaluator, _approvalEvaluator);
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
    public async Task CancelJob_WithExistingJob_CancelsAndReturns()
    {
        var jobRecord = CreateTestJobRecord("job-123", ExecutionJobStatus.Running);
        _jobStore.GetAsync("job-123", Arg.Any<CancellationToken>()).Returns(jobRecord);
        _cancellationNotifier.Cancel("job-123").Returns(true);

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
