// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Server.Features.Geoprocessing;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using Honua.Server.Tests.Helpers;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing;

/// <summary>
/// Unit tests for the shared <see cref="GeoprocessingJobService"/>
/// that backs both gRPC and REST adapters.
/// </summary>
[Protocol(Protocols.GPServer)]
public sealed class GeoprocessingJobServiceTests
{
    private readonly IExecutionJobStore _jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
    private readonly IJobQueue _jobQueue = Substitute.For<IJobQueue>();
    private readonly IUniversalProgressStore _progressStore = Substitute.For<IUniversalProgressStore>();
    private readonly IJobCancellationNotifier _cancellationNotifier = Substitute.For<IJobCancellationNotifier>();
    private readonly IOperatorAuthorizationEvaluator _authEvaluator = Substitute.For<IOperatorAuthorizationEvaluator>();
    private readonly IOperatorApprovalEvaluator _approvalEvaluator = Substitute.For<IOperatorApprovalEvaluator>();
    private readonly GeoprocessingJobService _sut;

    public GeoprocessingJobServiceTests()
    {
        _authEvaluator
            .Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>())
            .Returns(AccessDecision.Allowed());

        _approvalEvaluator
            .Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>())
            .Returns(ApprovalRequirement.NotRequired());

        _sut = new GeoprocessingJobService(
            _progressStore, [_cancellationNotifier],
            _authEvaluator, _approvalEvaluator,
            NullLogger<GeoprocessingJobService>.Instance,
            _jobStore, _jobQueue);
    }

    // -----------------------------------------------------------------------
    // ValidatePlan
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public void ValidatePlan_WithValidPlan_ReturnsExecutable()
    {
        var plan = CreateValidPlan();
        var result = _sut.ValidatePlan(plan, CreatePrincipal());

        result.IsExecutable.Should().BeTrue();
        result.Violations.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public void ValidatePlan_DoesNotCheckAuth_AdapterResponsibility()
    {
        // Auth is the adapter's responsibility (EnsureCallerAuthorized) so the
        // service method must succeed even when the evaluator would deny access.
        _authEvaluator
            .Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>())
            .Returns(AccessDecision.Forbidden());

        var result = _sut.ValidatePlan(CreateValidPlan(), CreatePrincipal());

        result.IsExecutable.Should().BeTrue();
    }

    // -----------------------------------------------------------------------
    // SubmitJobAsync
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_WithValidPlan_CreatesJobRecord()
    {
        _jobStore.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var plan = CreateValidPlan();
        var job = await _sut.SubmitJobAsync(plan, null, CreatePrincipal());

        job.OperationId.Should().NotBeNullOrWhiteSpace();
        job.Status.Should().Be(ExecutionJobStatus.Queued);
        job.Spec.Kind.Should().Be(ExecutionJobKind.Geoprocessing);
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_WithProtocolMetadata_StoresInSpecParameters()
    {
        _jobStore.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var metadata = new Dictionary<string, string>
        {
            ["gpserver.serviceId"] = "MyService",
            ["gpserver.taskName"] = "BufferAnalysis"
        };

        var plan = CreateValidPlan();
        var job = await _sut.SubmitJobAsync(plan, null, CreatePrincipal(), metadata);

        job.Spec.Parameters.Should().ContainKey("gpserver.serviceId").WhoseValue.Should().Be("MyService");
        job.Spec.Parameters.Should().ContainKey("gpserver.taskName").WhoseValue.Should().Be("BufferAnalysis");
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_WithoutProtocolMetadata_HasEmptySpecParameters()
    {
        _jobStore.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var plan = CreateValidPlan();
        var job = await _sut.SubmitJobAsync(plan, null, CreatePrincipal());

        job.Spec.Parameters.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_WithValidPlan_EnqueuesJob()
    {
        _jobStore.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var plan = CreateValidPlan();
        var job = await _sut.SubmitJobAsync(plan, null, CreatePrincipal());

        await _jobQueue.Received(1).EnqueueAsync(
            job.OperationId,
            Arg.Any<OperationPriority>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_WithoutJobStore_ThrowsStoreUnavailable()
    {
        var sut = new GeoprocessingJobService(
            _progressStore, [_cancellationNotifier],
            _authEvaluator, _approvalEvaluator,
            NullLogger<GeoprocessingJobService>.Instance,
            jobStore: null);

        var act = async () => await sut.SubmitJobAsync(CreateValidPlan(), null, CreatePrincipal());

        await act.Should().ThrowAsync<GeoprocessingStoreUnavailableException>();
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_ApprovalRequired_ThrowsApprovalException()
    {
        _approvalEvaluator
            .Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>())
            .Returns(ApprovalRequirement.Required("test-policy", "destructive-action"));

        var act = async () => await _sut.SubmitJobAsync(CreateValidPlan(), null, CreatePrincipal());

        await act.Should().ThrowAsync<GeoprocessingApprovalRequiredException>();
    }

    // -----------------------------------------------------------------------
    // GetJobAsync
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}")]
    public async Task GetJob_ExistingJob_ReturnsRecord()
    {
        var record = CreateJobRecord("job-1", ExecutionJobStatus.Running);
        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>()).Returns(record);

        var result = await _sut.GetJobAsync("job-1", CreatePrincipal());

        result.OperationId.Should().Be("job-1");
        result.Status.Should().Be(ExecutionJobStatus.Running);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}")]
    public async Task GetJob_MissingJob_ThrowsNotFound()
    {
        _jobStore.GetAsync("missing", Arg.Any<CancellationToken>()).Returns((ExecutionJobRecord?)null);

        var act = async () => await _sut.GetJobAsync("missing", CreatePrincipal());

        await act.Should().ThrowAsync<GeoprocessingNotFoundException>();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}")]
    public async Task GetJob_EmptyJobId_ThrowsValidation()
    {
        var act = async () => await _sut.GetJobAsync("", CreatePrincipal());

        await act.Should().ThrowAsync<GeoprocessingValidationException>();
    }

    // -----------------------------------------------------------------------
    // CancelJobAsync
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Delete)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    public async Task CancelJob_AlreadyCancelled_SucceedsIdempotently()
    {
        var record = CreateJobRecord("job-1", ExecutionJobStatus.Cancelled);
        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>()).Returns(record);

        await _sut.CancelJobAsync("job-1", CreatePrincipal());

        _cancellationNotifier.DidNotReceive().Cancel(Arg.Any<string>());
        await _jobQueue.Received(1).RemoveAsync("job-1", Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Delete)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    public async Task CancelJob_AlreadyCancelled_ReconcilesStalProgressStore()
    {
        var record = CreateJobRecord("job-1", ExecutionJobStatus.Cancelled);
        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>()).Returns(record);

        var staleProgress = GeoprocessingProgress.CreateForSubmittedJob("job-1", "plan-1");
        _progressStore.GetProgressAsync<GeoprocessingProgress>("job-1", Arg.Any<CancellationToken>())
            .Returns(staleProgress);

        await _sut.CancelJobAsync("job-1", CreatePrincipal());

        await _progressStore.Received(1).SetProgressAsync(
            "job-1",
            Arg.Is<Honua.Core.Features.Infrastructure.Domain.IOperationProgress>(p =>
                p.Status == Honua.Core.Features.Infrastructure.Domain.OperationStatus.Cancelled),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Delete)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    public async Task CancelJob_TerminalJob_ThrowsPreconditionFailed()
    {
        var record = CreateJobRecord("job-1", ExecutionJobStatus.Succeeded);
        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>()).Returns(record);

        var act = async () => await _sut.CancelJobAsync("job-1", CreatePrincipal());

        await act.Should().ThrowAsync<GeoprocessingPreconditionFailedException>();
    }

    [UnitTest]
    [Operation(Operations.Delete)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    public async Task CancelJob_ApprovalRequired_ThrowsApprovalException()
    {
        var record = CreateJobRecord("job-1", ExecutionJobStatus.Running);
        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>()).Returns(record);

        _approvalEvaluator
            .Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Is<OperatorAuthorizationRequest>(r => r.IsDestructive))
            .Returns(ApprovalRequirement.Required("destructive-policy", "destructive-action"));

        var act = async () => await _sut.CancelJobAsync("job-1", CreatePrincipal());

        await act.Should().ThrowAsync<GeoprocessingApprovalRequiredException>();
    }

    [UnitTest]
    [Operation(Operations.Delete)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    public async Task CancelJob_NoActiveWorker_RemovesFromQueue()
    {
        var record = CreateJobRecord("job-1", ExecutionJobStatus.Queued);
        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>()).Returns(record);
        _cancellationNotifier.Cancel("job-1").Returns(false);

        await _sut.CancelJobAsync("job-1", CreatePrincipal());

        await _jobQueue.Received(1).RemoveAsync("job-1", Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Delete)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    public async Task CancelJob_WorkerOwnsTerminalState_DoesNotCallRemove()
    {
        var record = CreateJobRecord("job-1", ExecutionJobStatus.Running);
        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>()).Returns(record);
        _cancellationNotifier.Cancel("job-1").Returns(true);

        await _sut.CancelJobAsync("job-1", CreatePrincipal());

        await _jobQueue.DidNotReceive().RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Delete)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    public async Task CancelJob_LaterNotifierOwnsTerminalState_DoesNotDependOnRegistrationOrder()
    {
        var record = CreateJobRecord("job-1", ExecutionJobStatus.Running);
        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>()).Returns(record);

        var firstNotifier = Substitute.For<IJobCancellationNotifier>();
        firstNotifier.Cancel("job-1").Returns(false);

        var secondNotifier = Substitute.For<IJobCancellationNotifier>();
        secondNotifier.Cancel("job-1").Returns(true);

        var sut = new GeoprocessingJobService(
            _progressStore,
            [firstNotifier, secondNotifier],
            _authEvaluator,
            _approvalEvaluator,
            NullLogger<GeoprocessingJobService>.Instance,
            _jobStore,
            _jobQueue);

        await sut.CancelJobAsync("job-1", CreatePrincipal());

        firstNotifier.Received(1).Cancel("job-1");
        secondNotifier.Received(1).Cancel("job-1");
        await _jobQueue.DidNotReceive().RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Delete)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    public async Task CancelJob_ReReadFindsSucceeded_ThrowsPreconditionFailed()
    {
        var queued = CreateJobRecord("job-1", ExecutionJobStatus.Queued);
        var succeeded = CreateJobRecord("job-1", ExecutionJobStatus.Succeeded);
        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>())
            .Returns(queued, succeeded);
        _cancellationNotifier.Cancel("job-1").Returns(false);

        var act = () => _sut.CancelJobAsync("job-1", CreatePrincipal());

        await act.Should().ThrowAsync<GeoprocessingPreconditionFailedException>();
        await _jobQueue.DidNotReceive().RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Delete)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    public async Task CancelJob_ReReadFindsCancelled_ReconcilesSideEffectsIdempotently()
    {
        var queued = CreateJobRecord("job-1", ExecutionJobStatus.Queued);
        var cancelled = CreateJobRecord("job-1", ExecutionJobStatus.Cancelled);
        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>())
            .Returns(queued, cancelled);
        _cancellationNotifier.Cancel("job-1").Returns(false);

        await _sut.CancelJobAsync("job-1", CreatePrincipal());

        await _jobQueue.Received(1).RemoveAsync("job-1", Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Delete)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    public async Task CancelJob_ReReadFindsDeleted_ThrowsNotFoundWithoutRecreatingJob()
    {
        var queued = CreateJobRecord("job-1", ExecutionJobStatus.Queued);
        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>())
            .Returns(queued, (ExecutionJobRecord?)null);
        _cancellationNotifier.Cancel("job-1").Returns(false);

        var act = () => _sut.CancelJobAsync("job-1", CreatePrincipal());

        await act.Should().ThrowAsync<GeoprocessingNotFoundException>();
        await _jobStore.DidNotReceive().SetAsync(
            Arg.Any<ExecutionJobRecord>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
        await _jobQueue.DidNotReceive().RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _progressStore.DidNotReceive().SetProgressAsync(
            Arg.Any<string>(),
            Arg.Any<Honua.Core.Features.Infrastructure.Domain.IOperationProgress>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Delete)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    public async Task CancelJob_ClaimedByRemoteWorker_SetsDurableCancellationSignal()
    {
        var record = CreateJobRecord("job-1", ExecutionJobStatus.Running) with
        {
            ClaimedBy = "worker-remote-1",
            ClaimedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            LastHeartbeatAt = DateTimeOffset.UtcNow.AddSeconds(-5)
        };
        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>()).Returns(record);
        _cancellationNotifier.Cancel("job-1").Returns(false);

        await _sut.CancelJobAsync("job-1", CreatePrincipal());

        await _jobStore.Received(1).TrySetAsync(
            Arg.Is<ExecutionJobRecord>(j =>
                j.OperationId == "job-1" &&
                j.CancellationRequestedAt.HasValue &&
                j.Status == ExecutionJobStatus.Running),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());

        await _jobQueue.DidNotReceive().RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Delete)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    public async Task CancelJob_UnclaimedJob_WritesCancelledDirectly()
    {
        var record = CreateJobRecord("job-1", ExecutionJobStatus.Queued);
        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>()).Returns(record);
        _cancellationNotifier.Cancel("job-1").Returns(false);

        await _sut.CancelJobAsync("job-1", CreatePrincipal());

        await _jobStore.Received(1).TrySetAsync(
            Arg.Is<ExecutionJobRecord>(j =>
                j.OperationId == "job-1" &&
                j.Status == ExecutionJobStatus.Cancelled),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());

        await _jobQueue.Received(1).RemoveAsync("job-1", Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Delete)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    public async Task CancelJob_AlreadyCancelled_ContinuesWhenQueueRemovalFails()
    {
        var record = CreateJobRecord("job-1", ExecutionJobStatus.Cancelled);
        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>()).Returns(record);
        _jobQueue.RemoveAsync("job-1", Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("Simulated Redis failure"));

        var staleProgress = GeoprocessingProgress.CreateForSubmittedJob("job-1", "plan-1");
        _progressStore.GetProgressAsync<GeoprocessingProgress>("job-1", Arg.Any<CancellationToken>())
            .Returns(staleProgress);

        await _sut.CancelJobAsync("job-1", CreatePrincipal());

        await _progressStore.Received(1).SetProgressAsync(
            "job-1",
            Arg.Is<Honua.Core.Features.Infrastructure.Domain.IOperationProgress>(p =>
                p.Status == Honua.Core.Features.Infrastructure.Domain.OperationStatus.Cancelled),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Delete)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    public async Task CancelJob_DirectCancel_ContinuesWhenQueueRemovalFails()
    {
        var record = CreateJobRecord("job-1", ExecutionJobStatus.Queued);
        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>()).Returns(record);
        _cancellationNotifier.Cancel("job-1").Returns(false);
        _jobQueue.RemoveAsync("job-1", Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("Simulated Redis failure"));

        await _sut.CancelJobAsync("job-1", CreatePrincipal());

        await _jobStore.Received(1).TrySetAsync(
            Arg.Is<ExecutionJobRecord>(j =>
                j.OperationId == "job-1" &&
                j.Status == ExecutionJobStatus.Cancelled),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Delete)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    public async Task CancelJob_ClaimedCasConflict_RetriesUntilSignalConfirmed()
    {
        var record = CreateJobRecord("job-1", ExecutionJobStatus.Running) with
        {
            ClaimedBy = "worker-remote-1",
            ClaimedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            LastHeartbeatAt = DateTimeOffset.UtcNow.AddSeconds(-5)
        };
        var freshRecord = record with { UpdatedAt = DateTimeOffset.UtcNow };
        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>())
            .Returns(record, record, freshRecord);
        _cancellationNotifier.Cancel("job-1").Returns(false);

        _jobStore.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(false, true);

        await _sut.CancelJobAsync("job-1", CreatePrincipal());

        await _jobStore.Received(2).TrySetAsync(
            Arg.Is<ExecutionJobRecord>(j =>
                j.OperationId == "job-1" &&
                j.CancellationRequestedAt.HasValue),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Delete)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    public async Task CancelJob_ClaimedCasConflict_ReReadShowsSignal_SucceedsWithoutRetry()
    {
        var record = CreateJobRecord("job-1", ExecutionJobStatus.Running) with
        {
            ClaimedBy = "worker-remote-1"
        };
        var conflictRecord = record with
        {
            CancellationRequestedAt = DateTimeOffset.UtcNow
        };
        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>())
            .Returns(record, record, conflictRecord);
        _cancellationNotifier.Cancel("job-1").Returns(false);

        _jobStore.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        await _sut.CancelJobAsync("job-1", CreatePrincipal());

        await _jobStore.Received(1).TrySetAsync(
            Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Delete)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    public async Task CancelJob_UnclaimedCasConflict_RetriesUntilCancelConfirmed()
    {
        var record = CreateJobRecord("job-1", ExecutionJobStatus.Queued);
        var freshRecord = record with { UpdatedAt = DateTimeOffset.UtcNow };
        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>())
            .Returns(record, record, freshRecord);
        _cancellationNotifier.Cancel("job-1").Returns(false);

        _jobStore.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(false, true);

        await _sut.CancelJobAsync("job-1", CreatePrincipal());

        await _jobStore.Received(2).TrySetAsync(
            Arg.Is<ExecutionJobRecord>(j =>
                j.OperationId == "job-1" &&
                j.Status == ExecutionJobStatus.Cancelled),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());

        await _jobQueue.Received(1).RemoveAsync("job-1", Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Delete)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    public async Task CancelJob_UnclaimedCasConflict_ReReadShowsSucceeded_ThrowsPrecondition()
    {
        var record = CreateJobRecord("job-1", ExecutionJobStatus.Queued);
        var succeededRecord = CreateJobRecord("job-1", ExecutionJobStatus.Succeeded);
        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>())
            .Returns(record, record, succeededRecord);
        _cancellationNotifier.Cancel("job-1").Returns(false);

        _jobStore.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var act = async () => await _sut.CancelJobAsync("job-1", CreatePrincipal());

        await act.Should().ThrowAsync<GeoprocessingPreconditionFailedException>();
        await _jobQueue.DidNotReceive().RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Delete)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    public async Task CancelJob_CasExhausted_ThrowsPreconditionFailed()
    {
        var record = CreateJobRecord("job-1", ExecutionJobStatus.Queued);
        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>()).Returns(record);
        _cancellationNotifier.Cancel("job-1").Returns(false);

        _jobStore.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var act = async () => await _sut.CancelJobAsync("job-1", CreatePrincipal());

        await act.Should().ThrowAsync<GeoprocessingPreconditionFailedException>()
            .WithMessage("*could not be confirmed*");
        await _jobQueue.DidNotReceive().RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Delete)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    public async Task CancelJob_UnclaimedCasConflict_JobBecomeClaimed_SwitchesToDurableSignal()
    {
        var record = CreateJobRecord("job-1", ExecutionJobStatus.Queued);
        var claimedRecord = record with
        {
            ClaimedBy = "worker-remote-1",
            ClaimedAt = DateTimeOffset.UtcNow,
            Status = ExecutionJobStatus.Running,
            LastHeartbeatAt = DateTimeOffset.UtcNow
        };

        // Initial reads return unclaimed; CAS fails; re-read shows claimed.
        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>())
            .Returns(record, record, claimedRecord);
        _cancellationNotifier.Cancel("job-1").Returns(false);

        _jobStore.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(false, true);

        await _sut.CancelJobAsync("job-1", CreatePrincipal());

        // Must have written CancellationRequestedAt, not terminal Cancelled.
        await _jobStore.Received().TrySetAsync(
            Arg.Is<ExecutionJobRecord>(j =>
                j.OperationId == "job-1" &&
                j.CancellationRequestedAt.HasValue &&
                j.Status != ExecutionJobStatus.Cancelled),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());

        // Queue should NOT have been cleaned up — worker owns the terminal state.
        await _jobQueue.DidNotReceive().RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_EnqueueFails_RollsBackJobToFailed()
    {
        ExecutionJobRecord? createdJob = null;
        _jobStore.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                createdJob = call.Arg<ExecutionJobRecord>();
                return true;
            });
        _jobStore.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => createdJob == null ? null : createdJob with { Version = 1 });
        _jobQueue.EnqueueAsync(Arg.Any<string>(), Arg.Any<OperationPriority>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("Redis unavailable"));

        var plan = new AnalysisPlan
        {
            PlanId = "plan-rollback",
            IntentId = "intent-1",
            Steps = [new AnalysisPlanStep { StepId = "s1", Kind = AnalysisPlanStepKind.Geoprocess, ProcessId = "buf" }]
        };

        var act = async () => await _sut.SubmitJobAsync(plan, null, CreatePrincipal());

        await act.Should().ThrowAsync<InvalidOperationException>();

        // The job record must have been rolled back to Failed.
        await _jobStore.Received().TrySetAsync(
            Arg.Is<ExecutionJobRecord>(j =>
                j.Status == ExecutionJobStatus.Failed &&
                j.Version == 1 &&
                j.ErrorMessage!.Contains("progress or queue persistence")),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_IdempotentRetryAfterSubmissionRollback_ThrowsInsteadOfReturningFailedRecord()
    {
        var plan = CreateValidPlan();
        var idempotencyKey = "retry-submission-rollback";
        var jobId = GeoprocessingJobService.CreateJobId(idempotencyKey);
        var requestFingerprint = GeoprocessingJobService.CreateRequestFingerprint(plan);

        var failedSubmission = CreateJobRecord(jobId, ExecutionJobStatus.Failed) with
        {
            CurrentPhase = "Failed (submission)",
            ErrorMessage = "Submission failed: progress or queue persistence error.",
            Audit = new OperationAuditInfo
            {
                IdempotencyKey = idempotencyKey,
                RequestFingerprint = requestFingerprint
            }
        };

        _jobStore.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _jobStore.GetAsync(jobId, Arg.Any<CancellationToken>())
            .Returns(failedSubmission);

        var act = async () => await _sut.SubmitJobAsync(plan, idempotencyKey, CreatePrincipal());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*previously failed before queueing*");
    }

    // -----------------------------------------------------------------------
    // ProcessId disambiguation
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public void CreateRequestFingerprint_DifferentServiceScopes_ProducesDifferentFingerprints()
    {
        var planA = new AnalysisPlan
        {
            PlanId = "plan-1",
            IntentId = "gpserver:ServiceA:BufferAnalysis",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "step-1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "ServiceA:BufferAnalysis"
                }
            ]
        };

        var planB = new AnalysisPlan
        {
            PlanId = "plan-1",
            IntentId = "gpserver:ServiceB:BufferAnalysis",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "step-1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "ServiceB:BufferAnalysis"
                }
            ]
        };

        var fingerprintA = GeoprocessingJobService.CreateRequestFingerprint(planA);
        var fingerprintB = GeoprocessingJobService.CreateRequestFingerprint(planB);

        fingerprintA.Should().NotBe(fingerprintB,
            "two services with the same task name must produce distinguishable process identities");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static AnalysisPlan CreateValidPlan() => new()
    {
        PlanId = "plan-1",
        IntentId = "intent-1",
        Steps =
        [
            new AnalysisPlanStep
            {
                StepId = "step-1",
                Kind = AnalysisPlanStepKind.Geoprocess,
                ProcessId = "buffer"
            }
        ]
    };

    private static ExecutionJobRecord CreateJobRecord(string jobId, ExecutionJobStatus status) => new()
    {
        OperationId = jobId,
        Status = status,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        Spec = new ExecutionJobSpec
        {
            Kind = ExecutionJobKind.Geoprocessing,
            TargetKind = BatchComputeTargetKind.KubernetesJob,
            Backend = "local",
            WorkloadName = "test-workload"
        }
    };

    private static ClaimsPrincipal CreatePrincipal()
        => new(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "test-user")], "Test"));
}
