// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Protocols.GeoServices.GPServer;
using Honua.Server.Features.Infrastructure.ControlPlane;
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
[Protocol(TestProtocols.GPServer)]
public sealed class GeoprocessingJobServiceTests
{
    private readonly IExecutionJobStore _jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
    private readonly IJobQueue _jobQueue = Substitute.For<IJobQueue>();
    private readonly IUniversalProgressStore _progressStore = Substitute.For<IUniversalProgressStore>();
    private readonly IJobCancellationNotifier _cancellationNotifier = Substitute.For<IJobCancellationNotifier>();
    private readonly IOperatorAuthorizationEvaluator _authEvaluator = Substitute.For<IOperatorAuthorizationEvaluator>();
    private readonly IOperatorApprovalEvaluator _approvalEvaluator = Substitute.For<IOperatorApprovalEvaluator>();
    private readonly IGeoprocessingResultPackageStore _resultPackageStore = Substitute.For<IGeoprocessingResultPackageStore>();
    private readonly GeoprocessingJobService _sut;

    public GeoprocessingJobServiceTests()
    {
        _authEvaluator
            .EvaluateAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AccessDecision.Allowed()));

        _approvalEvaluator
            .Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>())
            .Returns(ApprovalRequirement.NotRequired());

        _sut = new GeoprocessingJobService(
            _progressStore, [_cancellationNotifier],
            _authEvaluator, _approvalEvaluator,
            new BuiltInProcessCatalog(),
            NullLogger<GeoprocessingJobService>.Instance,
            _jobStore, _jobQueue,
            resultPackageStore: _resultPackageStore);
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
            .EvaluateAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AccessDecision.Forbidden()));

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
    public async Task SubmitJob_WithoutProtocolMetadata_StoresCanonicalPlanMetadata()
    {
        _jobStore.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var plan = CreateValidPlan();
        var job = await _sut.SubmitJobAsync(plan, null, CreatePrincipal());

        job.Spec.Parameters.Should().HaveCount(2);
        job.Spec.Parameters.Should().ContainKey(ExecutionJobParameterKeys.GeoprocessingPlanId)
            .WhoseValue.Should().Be("plan-1");
        job.Spec.Parameters.Should().ContainKey(ExecutionJobParameterKeys.GeoprocessingProcessDefinitions)
            .WhoseValue.Should().Be("geometry.buffer");
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
            new BuiltInProcessCatalog(),
            NullLogger<GeoprocessingJobService>.Instance,
            jobStore: null);

        var act = async () => await sut.SubmitJobAsync(CreateValidPlan(), null, CreatePrincipal());

        await act.Should().ThrowAsync<GeoprocessingStoreUnavailableException>();
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_UnknownProcessId_ThrowsValidation()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "plan-1",
            IntentId = "intent-1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "step-1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "unknown.process"
                }
            ]
        };

        var act = async () => await _sut.SubmitJobAsync(plan, null, CreatePrincipal());

        await act.Should().ThrowAsync<GeoprocessingValidationException>();
        await _jobStore.DidNotReceive().TryCreateAsync(
            Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_MissingRequiredParameter_ThrowsValidation()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "plan-1",
            IntentId = "intent-1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "step-1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "geometry.buffer",
                    Inputs = new Dictionary<string, string>
                    {
                        ["wkb"] = "AAAA"
                    }
                }
            ]
        };

        var act = async () => await _sut.SubmitJobAsync(plan, null, CreatePrincipal());

        await act.Should().ThrowAsync<GeoprocessingValidationException>();
        await _jobStore.DidNotReceive().TryCreateAsync(
            Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
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

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_WithConfiguredWorkload_SubmitsToMatchingBackendAndPersistsState()
    {
        var workloadRegistry = Substitute.For<IExecutionJobDefinitionRegistry>();
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        workloadRegistry.ListAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            new ExecutionJobDefinition
            {
                WorkloadId = "geoprocessing-remote",
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.AwsBatch,
                Backend = "aws-batch",
                WorkloadName = "Remote geoprocessing",
                ArtifactReference = "ecr/honua-gp:latest",
                RuntimeProfile = "py311",
                Parameters = new Dictionary<string, string>
                {
                    ["queue"] = "gp-primary"
                }
            }
        });
        backend.StartAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>())
            .Returns(new BatchComputeSubmissionResult
            {
                Status = ExecutionJobStatus.Provisioning,
                ProviderOperationId = "job-remote-123",
                Message = "Submitted to AWS Batch"
            });
        _jobStore.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var sut = new GeoprocessingJobService(
            _progressStore,
            [_cancellationNotifier],
            _authEvaluator,
            _approvalEvaluator,
            new BuiltInProcessCatalog(),
            NullLogger<GeoprocessingJobService>.Instance,
            _jobStore,
            workloadRegistry: workloadRegistry,
            backends: [backend]);

        var job = await sut.SubmitJobAsync(CreateValidPlan(), null, CreatePrincipal(), new Dictionary<string, string>
        {
            ["gpserver.serviceId"] = "TestService"
        });

        job.Status.Should().Be(ExecutionJobStatus.Provisioning);
        job.ProviderOperationId.Should().Be("job-remote-123");
        job.CurrentPhase.Should().Be("Submitted to AWS Batch");
        job.Spec.WorkloadId.Should().Be("geoprocessing-remote");
        job.Spec.Backend.Should().Be("aws-batch");
        job.Spec.TargetKind.Should().Be(BatchComputeTargetKind.AwsBatch);
        job.Spec.Parameters.Should().ContainKey("gpserver.serviceId").WhoseValue.Should().Be("TestService");
        job.Spec.Parameters.Should().ContainKey("queue").WhoseValue.Should().Be("gp-primary");
        job.Spec.Parameters.Should().ContainKey(ExecutionJobParameterKeys.GeoprocessingPlanId)
            .WhoseValue.Should().Be("plan-1");
        await _jobStore.Received().TrySetAsync(
            Arg.Is<ExecutionJobRecord>(record =>
                record.OperationId == job.OperationId &&
                record.Status == ExecutionJobStatus.Provisioning &&
                record.ProviderOperationId == "job-remote-123"),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_WithRemoteBackend_DoesNotEnqueueToLocalQueue()
    {
        var workloadRegistry = Substitute.For<IExecutionJobDefinitionRegistry>();
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        workloadRegistry.ListAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            new ExecutionJobDefinition
            {
                WorkloadId = "geoprocessing-remote",
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.AwsBatch,
                Backend = "aws-batch",
                WorkloadName = "Remote geoprocessing"
            }
        });
        backend.StartAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>())
            .Returns(new BatchComputeSubmissionResult
            {
                Status = ExecutionJobStatus.Provisioning,
                ProviderOperationId = "job-remote-456",
                Message = "Submitted to AWS Batch"
            });
        _jobStore.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var sut = new GeoprocessingJobService(
            _progressStore,
            [_cancellationNotifier],
            _authEvaluator,
            _approvalEvaluator,
            new BuiltInProcessCatalog(),
            NullLogger<GeoprocessingJobService>.Instance,
            _jobStore,
            _jobQueue,
            workloadRegistry: workloadRegistry,
            backends: [backend]);

        await sut.SubmitJobAsync(CreateValidPlan(), null, CreatePrincipal());

        await _jobQueue.DidNotReceive().EnqueueAsync(
            Arg.Any<string>(),
            Arg.Any<OperationPriority>(),
            Arg.Any<CancellationToken>());
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

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/GetJobResults")]
    public async Task GetJobResults_WithStoredResultPackage_ReturnsStoredPackage()
    {
        var record = CreateJobRecord("job-1", ExecutionJobStatus.Succeeded) with
        {
            Version = 7
        };
        var package = AnalysisResultPackage.CreateCompleted(
            GeoprocessingResultPackageFactory.CreateResultPackageId(record),
            new ResultSummary { Title = "Stored result" },
            [],
            [],
            new ProvenanceRecord
            {
                Sources = [],
                ProcessDefinitions = ["geometry.buffer"]
            });

        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>()).Returns(record);
        _resultPackageStore.GetAsync("job-1", Arg.Any<CancellationToken>()).Returns(package);

        var result = await _sut.GetJobResultsAsync("job-1", CreatePrincipal());

        result.Should().BeSameAs(package);
        await _resultPackageStore.DidNotReceive().SetAsync(
            Arg.Any<string>(),
            Arg.Any<AnalysisResultPackage>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/GetJobResults")]
    public async Task GetJobResults_WithSucceededTerminalJob_SynthesizesAndPersistsPackage()
    {
        var record = CreateJobRecord("job-1", ExecutionJobStatus.Succeeded) with
        {
            Version = 4,
            CompletedAt = DateTimeOffset.UtcNow,
            ArtifactReferences = ["https://example.test/artifacts/output.geojson"],
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = LocalBatchComputeBackend.BackendId,
                WorkloadName = "test-workload",
                Parameters = new Dictionary<string, string>
                {
                    [ExecutionJobParameterKeys.GeoprocessingPlanId] = "plan-1",
                    [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = "geometry.buffer",
                    [ExecutionJobParameterKeys.GeoprocessingOutputArtifactKinds] = "FeatureLayer",
                    [$"{GeoprocessingProtocolMetadataKeys.GPServerOutputNamePrefix}0"] = "outputFeatureLayer"
                }
            }
        };

        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>()).Returns(record);
        _resultPackageStore.GetAsync("job-1", Arg.Any<CancellationToken>())
            .Returns((AnalysisResultPackage?)null);

        var result = await _sut.GetJobResultsAsync("job-1", CreatePrincipal());

        result.Status.Should().Be(GeoprocessingWorkflowStatus.Completed);
        result.ResultPackageId.Should().Be("job-1:v4");
        result.Artifacts.Should().ContainSingle();
        result.Artifacts[0].Kind.Should().Be(ArtifactKind.FeatureLayer);
        result.Artifacts[0].Uri.Should().Be("https://example.test/artifacts/output.geojson");
        result.Artifacts[0].Metadata.Should().ContainKey(GPServerParameterTranslation.OutputParameterMetadataKey)
            .WhoseValue.Should().Be("outputFeatureLayer");
        await _resultPackageStore.Received(1).SetAsync(
            "job-1",
            Arg.Is<AnalysisResultPackage>(package =>
                package.ResultPackageId == "job-1:v4" &&
                package.Artifacts.Count == 1),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/GetJobResults")]
    public async Task GetJobResults_WithCancelledTerminalJob_SynthesizesCancelledPackage()
    {
        var record = CreateJobRecord("job-1", ExecutionJobStatus.Cancelled) with
        {
            Version = 3,
            CompletedAt = DateTimeOffset.UtcNow,
            ErrorMessage = "Cancelled by operator."
        };

        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>()).Returns(record);
        _resultPackageStore.GetAsync("job-1", Arg.Any<CancellationToken>())
            .Returns((AnalysisResultPackage?)null);

        var result = await _sut.GetJobResultsAsync("job-1", CreatePrincipal());

        result.Status.Should().Be(GeoprocessingWorkflowStatus.Cancelled);
        result.Errors.Should().ContainSingle(error => error.Kind == GeoprocessingErrorKind.Cancelled);
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
            new BuiltInProcessCatalog(),
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

        var validPlan = CreateValidPlan();
        var plan = new AnalysisPlan
        {
            PlanId = "plan-rollback",
            IntentId = validPlan.IntentId,
            Steps = validPlan.Steps
        };

        var act = async () => await _sut.SubmitJobAsync(plan, null, CreatePrincipal());

        await act.Should().ThrowAsync<InvalidOperationException>();

        // The job record must have been rolled back to Failed.
        await _jobStore.Received().TrySetAsync(
            Arg.Is<ExecutionJobRecord>(j =>
                j.Status == ExecutionJobStatus.Failed &&
                j.Version == 1 &&
                j.ErrorMessage!.Contains("Redis unavailable")),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Delete)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    public async Task CancelJob_RemoteBackendSupportingCancellation_DelegatesToBackend()
    {
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        backend.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new BatchComputeBackendCapabilities
        {
            SupportsCancellation = true
        });
        backend.CancelAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>())
            .Returns(new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Cancelled,
                Message = "Cancellation requested"
            });

        var record = CreateJobRecord(
            "job-1",
            ExecutionJobStatus.Running,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch);
        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>()).Returns(record);

        var sut = new GeoprocessingJobService(
            _progressStore,
            [_cancellationNotifier],
            _authEvaluator,
            _approvalEvaluator,
            new BuiltInProcessCatalog(),
            NullLogger<GeoprocessingJobService>.Instance,
            _jobStore,
            backends: [backend]);

        await sut.CancelJobAsync("job-1", CreatePrincipal());

        await backend.Received(1).CancelAsync(
            Arg.Is<ExecutionJobRecord>(job => job.OperationId == "job-1"),
            Arg.Any<CancellationToken>());
        await _jobStore.Received(1).TrySetAsync(
            Arg.Is<ExecutionJobRecord>(job =>
                job.OperationId == "job-1" &&
                job.Status == ExecutionJobStatus.Cancelled &&
                job.CompletedAt.HasValue),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Delete)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    public async Task CancelJob_RemoteQueuedNeverSubmitted_CancelsLocallyWithoutCallingBackend()
    {
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        backend.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new BatchComputeBackendCapabilities
        {
            SupportsCancellation = true
        });

        var record = CreateJobRecord(
            "job-1",
            ExecutionJobStatus.Queued,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch);
        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>()).Returns(record);

        var sut = new GeoprocessingJobService(
            _progressStore,
            [_cancellationNotifier],
            _authEvaluator,
            _approvalEvaluator,
            new BuiltInProcessCatalog(),
            NullLogger<GeoprocessingJobService>.Instance,
            _jobStore,
            backends: [backend]);

        await sut.CancelJobAsync("job-1", CreatePrincipal());

        await backend.DidNotReceive().CancelAsync(
            Arg.Any<ExecutionJobRecord>(),
            Arg.Any<CancellationToken>());
        await _jobStore.Received(1).TrySetAsync(
            Arg.Is<ExecutionJobRecord>(job =>
                job.OperationId == "job-1" &&
                job.Status == ExecutionJobStatus.Cancelled &&
                job.CompletedAt.HasValue &&
                job.CurrentPhase == "Cancelled before submission"),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Delete)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    public async Task CancelJob_RemoteQueuedNeverSubmitted_BridgesTerminalProgress()
    {
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        backend.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new BatchComputeBackendCapabilities
        {
            SupportsCancellation = true
        });

        var record = CreateJobRecord(
            "job-1",
            ExecutionJobStatus.Queued,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch);
        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>()).Returns(record);

        var sut = new GeoprocessingJobService(
            _progressStore,
            [_cancellationNotifier],
            _authEvaluator,
            _approvalEvaluator,
            new BuiltInProcessCatalog(),
            NullLogger<GeoprocessingJobService>.Instance,
            _jobStore,
            backends: [backend]);

        await sut.CancelJobAsync("job-1", CreatePrincipal());

        await _progressStore.Received().SetProgressAsync(
            "job-1",
            Arg.Any<GeoprocessingProgress>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Delete)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    public async Task CancelJob_RemoteQueuedNeverSubmitted_CasFailure_ReReadCancelled_StillBridgesAndSucceeds()
    {
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        backend.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new BatchComputeBackendCapabilities
        {
            SupportsCancellation = true
        });

        var record = CreateJobRecord(
            "job-1",
            ExecutionJobStatus.Queued,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch);
        var alreadyCancelled = record with
        {
            Status = ExecutionJobStatus.Cancelled,
            CompletedAt = DateTimeOffset.UtcNow
        };

        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>())
            .Returns(record, record, alreadyCancelled);
        _jobStore.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _cancellationNotifier.Cancel("job-1").Returns(false);

        var sut = new GeoprocessingJobService(
            _progressStore,
            [_cancellationNotifier],
            _authEvaluator,
            _approvalEvaluator,
            new BuiltInProcessCatalog(),
            NullLogger<GeoprocessingJobService>.Instance,
            _jobStore,
            backends: [backend]);

        await sut.CancelJobAsync("job-1", CreatePrincipal());

        await backend.DidNotReceive().CancelAsync(
            Arg.Any<ExecutionJobRecord>(),
            Arg.Any<CancellationToken>());
        await _progressStore.Received().SetProgressAsync(
            "job-1",
            Arg.Any<GeoprocessingProgress>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Delete)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    public async Task CancelJob_RemoteQueuedNeverSubmitted_CasFailure_ReReadTerminal_ThrowsPrecondition()
    {
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        backend.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new BatchComputeBackendCapabilities
        {
            SupportsCancellation = true
        });

        var record = CreateJobRecord(
            "job-1",
            ExecutionJobStatus.Queued,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch);
        var succeeded = record with
        {
            Status = ExecutionJobStatus.Succeeded,
            CompletedAt = DateTimeOffset.UtcNow
        };

        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>())
            .Returns(record, record, succeeded);
        _jobStore.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _cancellationNotifier.Cancel("job-1").Returns(false);

        var sut = new GeoprocessingJobService(
            _progressStore,
            [_cancellationNotifier],
            _authEvaluator,
            _approvalEvaluator,
            new BuiltInProcessCatalog(),
            NullLogger<GeoprocessingJobService>.Instance,
            _jobStore,
            backends: [backend]);

        var act = async () => await sut.CancelJobAsync("job-1", CreatePrincipal());

        await act.Should().ThrowAsync<GeoprocessingPreconditionFailedException>()
            .WithMessage("*terminal state*");
        await _progressStore.Received().SetProgressAsync(
            "job-1",
            Arg.Any<GeoprocessingProgress>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Delete)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    public async Task CancelJob_RemoteQueuedNeverSubmitted_CasFailure_ReReadMissing_ThrowsNotFound()
    {
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        backend.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new BatchComputeBackendCapabilities
        {
            SupportsCancellation = true
        });

        var record = CreateJobRecord(
            "job-1",
            ExecutionJobStatus.Queued,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch);

        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>())
            .Returns(record, record, (ExecutionJobRecord?)null);
        _jobStore.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _cancellationNotifier.Cancel("job-1").Returns(false);

        var sut = new GeoprocessingJobService(
            _progressStore,
            [_cancellationNotifier],
            _authEvaluator,
            _approvalEvaluator,
            new BuiltInProcessCatalog(),
            NullLogger<GeoprocessingJobService>.Instance,
            _jobStore,
            backends: [backend]);

        var act = async () => await sut.CancelJobAsync("job-1", CreatePrincipal());

        await act.Should().ThrowAsync<GeoprocessingNotFoundException>()
            .WithMessage("*deleted during cancellation*");
    }

    [UnitTest]
    [Operation(Operations.Delete)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    public async Task CancelJob_RemoteQueuedNeverSubmitted_CasFailure_ReReadNonTerminal_ThrowsPrecondition()
    {
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        backend.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new BatchComputeBackendCapabilities
        {
            SupportsCancellation = true
        });

        var record = CreateJobRecord(
            "job-1",
            ExecutionJobStatus.Queued,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch);
        var running = record with
        {
            Status = ExecutionJobStatus.Running,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>())
            .Returns(record, record, running);
        _jobStore.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _cancellationNotifier.Cancel("job-1").Returns(false);

        var sut = new GeoprocessingJobService(
            _progressStore,
            [_cancellationNotifier],
            _authEvaluator,
            _approvalEvaluator,
            new BuiltInProcessCatalog(),
            NullLogger<GeoprocessingJobService>.Instance,
            _jobStore,
            backends: [backend]);

        var act = async () => await sut.CancelJobAsync("job-1", CreatePrincipal());

        await act.Should().ThrowAsync<GeoprocessingPreconditionFailedException>()
            .WithMessage("*could not be confirmed*");
    }

    // Regression: a failed remote job that the reconciler re-queued for retry sits at
    // Queued + AttemptCount > 0 + NextRetryAt != null + ProviderOperationId == null. That is
    // semantically pre-submission for the next attempt (the provider object for the failed
    // attempt may already be TTL-cleaned), so cancel must take the local pre-submission
    // path and must not call backend.CancelAsync against the stale attempt.
    [UnitTest]
    [Operation(Operations.Delete)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    public async Task CancelJob_RemoteQueuedRetryAwaitingResubmission_CancelsLocallyWithoutCallingBackend()
    {
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        backend.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new BatchComputeBackendCapabilities
        {
            SupportsCancellation = true
        });

        var record = CreateJobRecord(
            "job-retry",
            ExecutionJobStatus.Queued,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch) with
        {
            AttemptCount = 1,
            ProviderOperationId = null,
            NextRetryAt = DateTimeOffset.UtcNow.AddSeconds(15),
            CurrentPhase = "Retrying (attempt 2/3)"
        };
        _jobStore.GetAsync("job-retry", Arg.Any<CancellationToken>()).Returns(record);

        var sut = new GeoprocessingJobService(
            _progressStore,
            [_cancellationNotifier],
            _authEvaluator,
            _approvalEvaluator,
            new BuiltInProcessCatalog(),
            NullLogger<GeoprocessingJobService>.Instance,
            _jobStore,
            backends: [backend]);

        await sut.CancelJobAsync("job-retry", CreatePrincipal());

        await backend.DidNotReceive().CancelAsync(
            Arg.Any<ExecutionJobRecord>(),
            Arg.Any<CancellationToken>());
        await _jobStore.Received(1).TrySetAsync(
            Arg.Is<ExecutionJobRecord>(job =>
                job.OperationId == "job-retry" &&
                job.Status == ExecutionJobStatus.Cancelled &&
                job.CompletedAt.HasValue &&
                job.CurrentPhase == "Cancelled before submission"),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.JobDismiss)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    public async Task CancelJob_RemoteBackendNonterminalResponse_PersistsCancellationRequestedAt()
    {
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        backend.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new BatchComputeBackendCapabilities
        {
            SupportsCancellation = true
        });
        backend.CancelAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>())
            .Returns(new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Running,
                Message = "Cancellation pending"
            });

        var record = CreateJobRecord(
            "job-1",
            ExecutionJobStatus.Running,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch);
        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>()).Returns(record);

        var sut = new GeoprocessingJobService(
            _progressStore,
            [_cancellationNotifier],
            _authEvaluator,
            _approvalEvaluator,
            new BuiltInProcessCatalog(),
            NullLogger<GeoprocessingJobService>.Instance,
            _jobStore,
            backends: [backend]);

        await sut.CancelJobAsync("job-1", CreatePrincipal());

        await _jobStore.Received().TrySetAsync(
            Arg.Is<ExecutionJobRecord>(job =>
                job.OperationId == "job-1" &&
                job.CancellationRequestedAt.HasValue &&
                job.Status == ExecutionJobStatus.Running),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());

        // Progress store must receive the nonterminal observation so admin /operations polling
        // reflects "Cancellation pending" without waiting for the reconciler.
        await _progressStore.Received().SetProgressAsync(
            "job-1",
            Arg.Is<GeoprocessingProgress>(p =>
                p.CurrentPhase == "Cancellation pending" &&
                p.WorkflowStatus == GeoprocessingWorkflowStatus.Running),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.JobDismiss)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    public async Task CancelJob_RemoteBackendNoCancellationSupport_ThrowsPreconditionFailed()
    {
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        backend.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new BatchComputeBackendCapabilities
        {
            SupportsCancellation = false
        });

        var record = CreateJobRecord(
            "job-1",
            ExecutionJobStatus.Running,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch);
        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>()).Returns(record);

        var sut = new GeoprocessingJobService(
            _progressStore,
            [_cancellationNotifier],
            _authEvaluator,
            _approvalEvaluator,
            new BuiltInProcessCatalog(),
            NullLogger<GeoprocessingJobService>.Instance,
            _jobStore,
            backends: [backend]);

        var act = async () => await sut.CancelJobAsync("job-1", CreatePrincipal());

        await act.Should().ThrowAsync<GeoprocessingPreconditionFailedException>()
            .WithMessage("*does not support cancellation*");
        await backend.DidNotReceive().CancelAsync(
            Arg.Any<ExecutionJobRecord>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.JobDismiss)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    public async Task CancelJob_RemoteBackendMissing_ThrowsPreconditionFailed()
    {
        var record = CreateJobRecord(
            "job-1",
            ExecutionJobStatus.Running,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch);
        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>()).Returns(record);

        var act = async () => await _sut.CancelJobAsync("job-1", CreatePrincipal());

        await act.Should().ThrowAsync<GeoprocessingPreconditionFailedException>()
            .WithMessage("*not registered*");
    }

    [UnitTest]
    [Operation(Operations.Delete)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    public async Task CancelJob_RemoteBackendReturnsSucceeded_ThrowsPreconditionFailed()
    {
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        backend.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new BatchComputeBackendCapabilities
        {
            SupportsCancellation = true
        });
        backend.CancelAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>())
            .Returns(new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Succeeded,
                Message = "Job already completed"
            });

        var record = CreateJobRecord(
            "job-1",
            ExecutionJobStatus.Running,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch);
        var succeededRecord = record with
        {
            Status = ExecutionJobStatus.Succeeded,
            CompletedAt = DateTimeOffset.UtcNow
        };
        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>())
            .Returns(record, record, succeededRecord);

        var sut = new GeoprocessingJobService(
            _progressStore,
            [_cancellationNotifier],
            _authEvaluator,
            _approvalEvaluator,
            new BuiltInProcessCatalog(),
            NullLogger<GeoprocessingJobService>.Instance,
            _jobStore,
            backends: [backend]);

        var act = async () => await sut.CancelJobAsync("job-1", CreatePrincipal());

        await act.Should().ThrowAsync<GeoprocessingPreconditionFailedException>()
            .WithMessage("*terminal state*");
    }

    [UnitTest]
    [Operation(Operations.Delete)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    public async Task CancelJob_RemoteBackendReturnsFailed_ThrowsPreconditionFailed()
    {
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        backend.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new BatchComputeBackendCapabilities
        {
            SupportsCancellation = true
        });
        backend.CancelAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>())
            .Returns(new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Failed,
                Message = "Job already failed"
            });

        var record = CreateJobRecord(
            "job-1",
            ExecutionJobStatus.Running,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch);
        var failedRecord = record with
        {
            Status = ExecutionJobStatus.Failed,
            CompletedAt = DateTimeOffset.UtcNow
        };
        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>())
            .Returns(record, record, failedRecord);

        var sut = new GeoprocessingJobService(
            _progressStore,
            [_cancellationNotifier],
            _authEvaluator,
            _approvalEvaluator,
            new BuiltInProcessCatalog(),
            NullLogger<GeoprocessingJobService>.Instance,
            _jobStore,
            backends: [backend]);

        var act = async () => await sut.CancelJobAsync("job-1", CreatePrincipal());

        await act.Should().ThrowAsync<GeoprocessingPreconditionFailedException>()
            .WithMessage("*terminal state*");
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_RemoteBackend_TransitionsToProvisioningBeforeStartAsync()
    {
        var workloadRegistry = Substitute.For<IExecutionJobDefinitionRegistry>();
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        workloadRegistry.ListAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            new ExecutionJobDefinition
            {
                WorkloadId = "geoprocessing-remote",
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.AwsBatch,
                Backend = "aws-batch",
                WorkloadName = "Remote geoprocessing"
            }
        });

        ExecutionJobRecord? recordPassedToStart = null;
        backend.StartAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                recordPassedToStart = call.Arg<ExecutionJobRecord>();
                return new BatchComputeSubmissionResult
                {
                    Status = ExecutionJobStatus.Running,
                    ProviderOperationId = "provider-1",
                    Message = "Started"
                };
            });
        _jobStore.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var sut = new GeoprocessingJobService(
            _progressStore,
            [_cancellationNotifier],
            _authEvaluator,
            _approvalEvaluator,
            new BuiltInProcessCatalog(),
            NullLogger<GeoprocessingJobService>.Instance,
            _jobStore,
            workloadRegistry: workloadRegistry,
            backends: [backend]);

        await sut.SubmitJobAsync(CreateValidPlan(), null, CreatePrincipal());

        recordPassedToStart.Should().NotBeNull();
        recordPassedToStart!.Status.Should().Be(ExecutionJobStatus.Provisioning,
            "the job must transition to Provisioning before StartAsync so the reconciler does not double-start it");

        await _jobStore.Received().TrySetAsync(
            Arg.Is<ExecutionJobRecord>(j =>
                j.Status == ExecutionJobStatus.Provisioning &&
                string.IsNullOrEmpty(j.ProviderOperationId)),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_RemoteBackendStartFails_RollsBackJobToFailed()
    {
        var workloadRegistry = Substitute.For<IExecutionJobDefinitionRegistry>();
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        workloadRegistry.ListAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            new ExecutionJobDefinition
            {
                WorkloadId = "geoprocessing-remote",
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.AwsBatch,
                Backend = "aws-batch",
                WorkloadName = "Remote geoprocessing"
            }
        });
        backend.StartAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>())
            .Returns<Task<BatchComputeSubmissionResult>>(_ => throw new InvalidOperationException("Backend unavailable"));

        ExecutionJobRecord? createdJob = null;
        _jobStore.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                createdJob = call.Arg<ExecutionJobRecord>();
                return true;
            });
        _jobStore.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => createdJob == null ? null : createdJob with { Version = 1 });

        var sut = new GeoprocessingJobService(
            _progressStore,
            [_cancellationNotifier],
            _authEvaluator,
            _approvalEvaluator,
            new BuiltInProcessCatalog(),
            NullLogger<GeoprocessingJobService>.Instance,
            _jobStore,
            workloadRegistry: workloadRegistry,
            backends: [backend]);

        var act = async () => await sut.SubmitJobAsync(CreateValidPlan(), null, CreatePrincipal());

        await act.Should().ThrowAsync<InvalidOperationException>();

        await _jobStore.Received().TrySetAsync(
            Arg.Is<ExecutionJobRecord>(j =>
                j.Status == ExecutionJobStatus.Failed &&
                j.ErrorMessage!.Contains("Backend unavailable")),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_AdmissionDenied_ThrowsAdmissionException()
    {
        _jobStore.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var admission = Substitute.For<IExecutionAdmissionEvaluator>();
        admission.EvaluateAsync(Arg.Any<ExecutionAdmissionRequest>(), Arg.Any<CancellationToken>())
            .Returns(ExecutionAdmissionDecision.Denied(
                ExecutionAdmissionDimension.Concurrency,
                "concurrency:geoprocessing:per-partition",
                "Partition active job limit reached.",
                retryAfterSeconds: 15,
                new ExecutionAdmissionSnapshot { ActiveJobsInPartition = 10 }));

        var sut = new GeoprocessingJobService(
            _progressStore,
            [_cancellationNotifier],
            _authEvaluator,
            _approvalEvaluator,
            new BuiltInProcessCatalog(),
            NullLogger<GeoprocessingJobService>.Instance,
            _jobStore,
            admissionEvaluator: admission);

        var act = async () => await sut.SubmitJobAsync(CreateValidPlan(), null, CreatePrincipal());

        var ex = await act.Should().ThrowAsync<GeoprocessingAdmissionException>();
        ex.Which.Outcome.Should().Be(ExecutionAdmissionOutcome.Denied);
        ex.Which.DenyingDimension.Should().Be(ExecutionAdmissionDimension.Concurrency);
        ex.Which.RetryAfterSeconds.Should().Be(15);

        await _jobStore
            .DidNotReceive()
            .TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_WhenAdmitted_PersistsCostWeightInSpecParameters()
    {
        _jobStore.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var admission = Substitute.For<IExecutionAdmissionEvaluator>();
        admission.EvaluateAsync(Arg.Any<ExecutionAdmissionRequest>(), Arg.Any<CancellationToken>())
            .Returns(ExecutionAdmissionDecision.Admitted(new ExecutionAdmissionSnapshot()));

        var sut = new GeoprocessingJobService(
            _progressStore,
            [_cancellationNotifier],
            _authEvaluator,
            _approvalEvaluator,
            new BuiltInProcessCatalog(),
            NullLogger<GeoprocessingJobService>.Instance,
            _jobStore,
            admissionEvaluator: admission);

        var metadata = new Dictionary<string, string> { ["workspace.id"] = "ws-42" };
        var job = await sut.SubmitJobAsync(CreateValidPlan(), null, CreatePrincipal(), metadata);

        job.Spec.Parameters.Should().ContainKey(ExecutionAdmissionEvaluator.CostWeightParameterKey);
        job.Spec.Parameters.Should().ContainKey(ExecutionAdmissionEvaluator.PartitionKeyParameterKey)
            .WhoseValue.Should().Be("ws-42");
    }

    // -----------------------------------------------------------------------
    // GetJobAsync
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Delete)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    public async Task CancelJob_RemoteBackendCasConflict_RetriesWithFreshRecord()
    {
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        backend.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new BatchComputeBackendCapabilities
        {
            SupportsCancellation = true
        });
        backend.CancelAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>())
            .Returns(new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Cancelled,
                Message = "Cancellation confirmed"
            });

        // Pre-stamp CancellationRequestedAt so the remote cancel path skips the stamp
        // helper and exercises TryApplyBackendCancelAsync's CAS-retry loop directly.
        var record = CreateJobRecord(
            "job-1",
            ExecutionJobStatus.Running,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch) with
        {
            CancellationRequestedAt = DateTimeOffset.UtcNow.AddSeconds(-10)
        };
        var freshRecord = record with { UpdatedAt = DateTimeOffset.UtcNow };
        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>())
            .Returns(record, record, freshRecord);

        _jobStore.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(false, true);

        var sut = new GeoprocessingJobService(
            _progressStore,
            [_cancellationNotifier],
            _authEvaluator,
            _approvalEvaluator,
            new BuiltInProcessCatalog(),
            NullLogger<GeoprocessingJobService>.Instance,
            _jobStore,
            backends: [backend]);

        await sut.CancelJobAsync("job-1", CreatePrincipal());

        await _jobStore.Received(2).TrySetAsync(
            Arg.Is<ExecutionJobRecord>(job =>
                job.OperationId == "job-1" &&
                job.Status == ExecutionJobStatus.Cancelled),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.JobDismiss)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    public async Task CancelJob_RemoteBackendCasConflictWithTerminalWinner_FallsThrough()
    {
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        backend.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new BatchComputeBackendCapabilities
        {
            SupportsCancellation = true
        });
        backend.CancelAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>())
            .Returns(new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Cancelled,
                Message = "Cancellation confirmed"
            });

        var record = CreateJobRecord(
            "job-1",
            ExecutionJobStatus.Running,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch);
        var terminalRecord = record with
        {
            Status = ExecutionJobStatus.Succeeded,
            UpdatedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow
        };
        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>())
            .Returns(record, record, terminalRecord);
        _jobStore.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var sut = new GeoprocessingJobService(
            _progressStore,
            [_cancellationNotifier],
            _authEvaluator,
            _approvalEvaluator,
            new BuiltInProcessCatalog(),
            NullLogger<GeoprocessingJobService>.Instance,
            _jobStore,
            backends: [backend]);

        var act = async () => await sut.CancelJobAsync("job-1", CreatePrincipal());

        await act.Should().ThrowAsync<GeoprocessingPreconditionFailedException>()
            .WithMessage("*terminal state*");
        await _progressStore.Received().SetProgressAsync(
            "job-1",
            Arg.Any<GeoprocessingProgress>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_RemoteBackendPostStartCasConflict_ReturnsStoreRecord()
    {
        var workloadRegistry = Substitute.For<IExecutionJobDefinitionRegistry>();
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        workloadRegistry.ListAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            new ExecutionJobDefinition
            {
                WorkloadId = "geoprocessing-remote",
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.AwsBatch,
                Backend = "aws-batch",
                WorkloadName = "Remote geoprocessing"
            }
        });
        backend.StartAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>())
            .Returns(new BatchComputeSubmissionResult
            {
                Status = ExecutionJobStatus.Running,
                ProviderOperationId = "provider-cas-conflict",
                Message = "Started"
            });
        _jobStore.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var callCount = 0;
        _jobStore.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                callCount++;
                // First CAS (Provisioning transition) succeeds; second CAS (post-start) fails.
                return callCount != 2;
            });

        var cancelledByReconciler = CreateJobRecord(
            "will-be-overwritten",
            ExecutionJobStatus.Cancelled,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch);
        _jobStore.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(cancelledByReconciler);

        var sut = new GeoprocessingJobService(
            _progressStore,
            [_cancellationNotifier],
            _authEvaluator,
            _approvalEvaluator,
            new BuiltInProcessCatalog(),
            NullLogger<GeoprocessingJobService>.Instance,
            _jobStore,
            workloadRegistry: workloadRegistry,
            backends: [backend]);

        var job = await sut.SubmitJobAsync(CreateValidPlan(), null, CreatePrincipal());

        job.Status.Should().Be(ExecutionJobStatus.Cancelled,
            "on CAS conflict the authoritative store record must be returned, not the stale submission result");
    }

    [UnitTest]
    [Operation(Operations.Delete)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    public async Task CancelJob_RemoteBackendDoubleCasMiss_ThrowsUnconfirmed()
    {
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        backend.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new BatchComputeBackendCapabilities
        {
            SupportsCancellation = true
        });
        backend.CancelAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>())
            .Returns(new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Cancelled,
                Message = "Cancellation confirmed"
            });

        var record = CreateJobRecord(
            "job-1",
            ExecutionJobStatus.Running,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch);
        var freshRecord = record with { UpdatedAt = DateTimeOffset.UtcNow };
        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>())
            .Returns(record, record, freshRecord);
        _jobStore.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var sut = new GeoprocessingJobService(
            _progressStore,
            [_cancellationNotifier],
            _authEvaluator,
            _approvalEvaluator,
            new BuiltInProcessCatalog(),
            NullLogger<GeoprocessingJobService>.Instance,
            _jobStore,
            backends: [backend]);

        var act = async () => await sut.CancelJobAsync("job-1", CreatePrincipal());

        await act.Should().ThrowAsync<GeoprocessingPreconditionFailedException>()
            .WithMessage("*could not be confirmed*");
    }

    [UnitTest]
    [Operation(Operations.Delete)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    public async Task CancelJob_RemoteBackendJobDeletedDuringCas_ThrowsNotFound()
    {
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        backend.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new BatchComputeBackendCapabilities
        {
            SupportsCancellation = true
        });
        backend.CancelAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>())
            .Returns(new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Cancelled,
                Message = "Cancellation confirmed"
            });

        var record = CreateJobRecord(
            "job-1",
            ExecutionJobStatus.Running,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch);
        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>())
            .Returns(record, record, null);
        _jobStore.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var sut = new GeoprocessingJobService(
            _progressStore,
            [_cancellationNotifier],
            _authEvaluator,
            _approvalEvaluator,
            new BuiltInProcessCatalog(),
            NullLogger<GeoprocessingJobService>.Instance,
            _jobStore,
            backends: [backend]);

        var act = async () => await sut.CancelJobAsync("job-1", CreatePrincipal());

        await act.Should().ThrowAsync<GeoprocessingNotFoundException>()
            .WithMessage("*deleted*");
    }

    [UnitTest]
    [Operation(Operations.Delete)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    public async Task CancelJob_RemoteBackendReReadFindsTerminal_DoesNotCallBackendCancel()
    {
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        backend.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new BatchComputeBackendCapabilities
        {
            SupportsCancellation = true
        });

        var running = CreateJobRecord(
            "job-1",
            ExecutionJobStatus.Running,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch);
        var terminal = running with
        {
            Status = ExecutionJobStatus.Succeeded,
            CompletedAt = DateTimeOffset.UtcNow
        };

        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>())
            .Returns(running, terminal, terminal);

        var sut = new GeoprocessingJobService(
            _progressStore,
            [_cancellationNotifier],
            _authEvaluator,
            _approvalEvaluator,
            new BuiltInProcessCatalog(),
            NullLogger<GeoprocessingJobService>.Instance,
            _jobStore,
            backends: [backend]);

        var act = async () => await sut.CancelJobAsync("job-1", CreatePrincipal());

        await act.Should().ThrowAsync<GeoprocessingPreconditionFailedException>()
            .WithMessage("*terminal state*");

        await backend.DidNotReceive().CancelAsync(
            Arg.Any<ExecutionJobRecord>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Delete)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    public async Task CancelJob_RemoteBackendReReadFindsCancelled_BridgesProgressWithoutBackendCall()
    {
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        backend.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new BatchComputeBackendCapabilities
        {
            SupportsCancellation = true
        });

        var running = CreateJobRecord(
            "job-1",
            ExecutionJobStatus.Running,
            backend: "aws-batch",
            targetKind: BatchComputeTargetKind.AwsBatch);
        var cancelled = running with
        {
            Status = ExecutionJobStatus.Cancelled,
            CompletedAt = DateTimeOffset.UtcNow
        };

        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>())
            .Returns(running, cancelled);
        _cancellationNotifier.Cancel("job-1").Returns(false);

        var staleProgress = GeoprocessingProgress.CreateForSubmittedJob("job-1", "plan-1");
        _progressStore.GetProgressAsync<GeoprocessingProgress>("job-1", Arg.Any<CancellationToken>())
            .Returns(staleProgress);

        var sut = new GeoprocessingJobService(
            _progressStore,
            [_cancellationNotifier],
            _authEvaluator,
            _approvalEvaluator,
            new BuiltInProcessCatalog(),
            NullLogger<GeoprocessingJobService>.Instance,
            _jobStore,
            backends: [backend]);

        await sut.CancelJobAsync("job-1", CreatePrincipal());

        await backend.DidNotReceive().CancelAsync(
            Arg.Any<ExecutionJobRecord>(),
            Arg.Any<CancellationToken>());
        await _progressStore.Received(1).SetProgressAsync(
            "job-1",
            Arg.Is<Honua.Core.Features.Infrastructure.Domain.IOperationProgress>(p =>
                p.Status == Honua.Core.Features.Infrastructure.Domain.OperationStatus.Cancelled),
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
                ProcessId = "geometry.buffer",
                Inputs = new Dictionary<string, string>
                {
                    ["wkb"] = "AAAA",
                    ["srid"] = "4326",
                    ["distance"] = "100"
                }
            }
        ]
    };

    private static ExecutionJobRecord CreateJobRecord(
        string jobId,
        ExecutionJobStatus status,
        string backend = LocalBatchComputeBackend.BackendId,
        BatchComputeTargetKind targetKind = BatchComputeTargetKind.KubernetesJob) => new()
        {
            OperationId = jobId,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = targetKind,
                Backend = backend,
                WorkloadName = "test-workload"
            }
        };

    private static ClaimsPrincipal CreatePrincipal()
        => new(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "test-user")], "Test"));
}
