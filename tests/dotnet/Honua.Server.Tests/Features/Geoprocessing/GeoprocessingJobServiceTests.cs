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
using Honua.Geoprocessing;
using Honua.Protocols.GeoServices.GPServer;
using Honua.ControlPlane;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Honua.TestKit.Helpers;
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
            DefaultExecutorOptions,
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
        // Auth is the adapter's responsibility (EnsureCallerAuthorizedAsync) so the
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

        // Plan-level metadata: plan id + process definitions.
        job.Spec.Parameters.Should().ContainKey(ExecutionJobParameterKeys.GeoprocessingPlanId)
            .WhoseValue.Should().Be("plan-1");
        job.Spec.Parameters.Should().ContainKey(ExecutionJobParameterKeys.GeoprocessingProcessDefinitions)
            .WhoseValue.Should().Be("geometry.buffer");

        // Step-level inputs are projected under a stable prefix so worker-side
        // executors can read parameters without re-fetching the plan (#1031).
        job.Spec.Parameters.Should().ContainKey($"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.wkb")
            .WhoseValue.Should().Be("AAAA");
        job.Spec.Parameters.Should().ContainKey($"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.srid")
            .WhoseValue.Should().Be("4326");
        job.Spec.Parameters.Should().ContainKey($"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.distance")
            .WhoseValue.Should().Be("100");
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_ManagedPlan_LeavesRuntimeProfileUnstamped()
    {
        _jobStore.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        // A managed process (geometry.buffer) declares the managed/default catalog
        // profile, so the submit path leaves the spec profile-agnostic (null) and the
        // lean dispatcher claims it.
        var job = await _sut.SubmitJobAsync(CreateValidPlan(), null, CreatePrincipal());

        job.Spec.RuntimeProfile.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_NativeGdalPlan_StampsNativeRuntimeProfile()
    {
        _jobStore.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        // The data-driven stamping contract: a gdal.* process declares
        // RuntimeProfile = native in the catalog, so the submit path stamps the spec
        // and the claim fence routes the job to the out-of-process GDAL worker
        // (never to the lean GDAL-free dispatcher).
        var plan = new AnalysisPlan
        {
            PlanId = "plan-native",
            IntentId = "intent-native",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "step-1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "gdal.gdalwarp",
                    Inputs = new Dictionary<string, string>
                    {
                        ["source"] = "AAAA",
                        ["targetSrs"] = "3857"
                    }
                }
            ]
        };

        var job = await _sut.SubmitJobAsync(plan, null, CreatePrincipal());

        job.Spec.RuntimeProfile.Should().Be(RuntimeProfiles.Native);
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_ManagedReprojectFastPath_LeavesRuntimeProfileUnstamped()
    {
        _jobStore.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        // 4326 -> 3857 is a managed in-memory fast path: no datum shift, so the job
        // stays on the lean profile and the managed ReprojectTransformExecutor serves
        // it. The submit path must NOT escalate this to native.
        var plan = CreateReprojectPlan(fromSrid: "4326", toSrid: "3857");

        var job = await _sut.SubmitJobAsync(plan, null, CreatePrincipal());

        job.Spec.RuntimeProfile.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_DatumShiftReproject_EscalatesToNativeRuntimeProfile()
    {
        _jobStore.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        // NAD 27 (4267) -> WGS 84 (4326) is a datum shift the managed path rejects.
        // Even though transform.reproject declares the managed catalog profile, the
        // submit path inspects the SRID inputs and ESCALATES the job to native so the
        // claim fence routes it to the PROJ-backed GdalVectorReprojectJobExecutor.
        var plan = CreateReprojectPlan(fromSrid: "4267", toSrid: "4326");

        var job = await _sut.SubmitJobAsync(plan, null, CreatePrincipal());

        job.Spec.RuntimeProfile.Should().Be(RuntimeProfiles.Native);
    }

    private static AnalysisPlan CreateReprojectPlan(string fromSrid, string toSrid)
    {
        // A minimal canonical geo+json data URI satisfying transform.reproject's
        // required 'input'. The submit path's escalation reads only the SRID inputs.
        const string emptyFeatureCollection =
            "data:application/geo+json;base64," +
            "eyJ0eXBlIjoiRmVhdHVyZUNvbGxlY3Rpb24iLCJmZWF0dXJlcyI6W119"; // {"type":"FeatureCollection","features":[]}

        return new AnalysisPlan
        {
            PlanId = "plan-reproject",
            IntentId = "intent-reproject",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "step-1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "transform.reproject",
                    Inputs = new Dictionary<string, string>
                    {
                        ["input"] = emptyFeatureCollection,
                        ["fromSrid"] = fromSrid,
                        ["toSrid"] = toSrid
                    }
                }
            ]
        };
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
            DefaultExecutorOptions,
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
    public async Task SubmitJob_SinkProcessRequiresApproval_ThrowsApprovalException()
    {
        // A sink/write process must pass the same approval gate as a destructive
        // mutation (#2262). The evaluator requires approval only when the request
        // is flagged destructive, so this asserts the submit path raises that flag
        // for a sink plan that materializes data into a caller-owned destination.
        _approvalEvaluator
            .Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Is<OperatorAuthorizationRequest>(r => r.IsDestructive))
            .Returns(ApprovalRequirement.Required("operator.destructive.process", "destructive-action-requires-approval"));

        var act = async () => await _sut.SubmitJobAsync(CreateSinkPlan(), null, CreatePrincipal());

        await act.Should().ThrowAsync<GeoprocessingApprovalRequiredException>();
        await _jobStore.DidNotReceive().TryCreateAsync(
            Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_CallerNotAuthorized_ThrowsAuthorizationException()
    {
        // The submit path centralizes Process/Execute authorization (#2263) so a
        // forbidden caller is rejected before any job record is created, regardless
        // of which adapter (or none) authorized first.
        _authEvaluator
            .EvaluateAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AccessDecision.Forbidden()));

        var act = async () => await _sut.SubmitJobAsync(CreateValidPlan(), null, CreatePrincipal());

        await act.Should().ThrowAsync<GeoprocessingAuthorizationException>();
        await _jobStore.DidNotReceive().TryCreateAsync(
            Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
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
            DefaultExecutorOptions,
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
            DefaultExecutorOptions,
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

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_WithLocalAndBatchWorkloads_PrefersBatchAndCarriesTierParams()
    {
        // Both the always-present local baseline AND a fully-configured AWS Batch GP
        // workload are registered. The service must prefer the Batch workload and
        // route the job to the honua-aws-batch backend, carrying the queue + tier ARNs.
        var workloadRegistry = Substitute.For<IExecutionJobDefinitionRegistry>();
        var batchBackend = Substitute.For<IBatchComputeBackend>();
        batchBackend.BackendName.Returns("honua-aws-batch");
        batchBackend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        workloadRegistry.ListAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            new ExecutionJobDefinition
            {
                WorkloadId = "geoprocessing-local",
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "Geoprocessing (local baseline)"
            },
            new ExecutionJobDefinition
            {
                WorkloadId = "geoprocessing-aws-batch",
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.AwsBatch,
                Backend = "honua-aws-batch",
                WorkloadName = "Geoprocessing (AWS Batch)",
                Parameters = new Dictionary<string, string>
                {
                    ["batch.job_queue_arn"] = "arn:aws:batch:us-east-1:123:job-queue/honua-gp",
                    ["batch.region"] = "us-east-1",
                    ["batch.job_definition_arn.s"] = "arn:aws:batch:us-east-1:123:job-definition/honua-gp-s:1",
                    ["batch.job_definition_arn.m"] = "arn:aws:batch:us-east-1:123:job-definition/honua-gp-m:1",
                    ["batch.job_definition_arn.l"] = "arn:aws:batch:us-east-1:123:job-definition/honua-gp-l:1",
                    ["batch.job_definition_arn.xl"] = "arn:aws:batch:us-east-1:123:job-definition/honua-gp-xl:1"
                }
            }
        });
        batchBackend.StartAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>())
            .Returns(new BatchComputeSubmissionResult
            {
                Status = ExecutionJobStatus.Provisioning,
                ProviderOperationId = "job-batch-789",
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
            DefaultExecutorOptions,
            _jobStore,
            _jobQueue,
            workloadRegistry: workloadRegistry,
            backends: [batchBackend]);

        var job = await sut.SubmitJobAsync(CreateValidPlan(), null, CreatePrincipal());

        job.Spec.WorkloadId.Should().Be("geoprocessing-aws-batch");
        job.Spec.Backend.Should().Be("honua-aws-batch");
        job.Spec.TargetKind.Should().Be(BatchComputeTargetKind.AwsBatch);
        job.Spec.Parameters.Should().ContainKey("batch.job_queue_arn")
            .WhoseValue.Should().Be("arn:aws:batch:us-east-1:123:job-queue/honua-gp");
        job.Spec.Parameters.Should().ContainKey("batch.region").WhoseValue.Should().Be("us-east-1");
        job.Spec.Parameters.Should().ContainKey("batch.job_definition_arn.s");
        job.Spec.Parameters.Should().ContainKey("batch.job_definition_arn.xl");
        await batchBackend.Received().StartAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>());
        await _jobQueue.DidNotReceive().EnqueueAsync(
            Arg.Any<string>(),
            Arg.Any<OperationPriority>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_WithBatchWorkload_CarriesPerJobResourceProfileSizing()
    {
        // The per-job resource profile derived from the plan's process (geometry.buffer is
        // managed → smallest tier) must be projected onto the spec's batch.* params so
        // AwsBatchComputeBackend sizes SubmitJob and selects the ephemeral tier per job.
        var sut = BuildBatchWorkloadService(out _);

        var job = await sut.SubmitJobAsync(CreateValidPlan(), null, CreatePrincipal());

        job.Spec.Parameters.Should().ContainKey("batch.vcpus").WhoseValue.Should().Be("1");
        job.Spec.Parameters.Should().ContainKey("batch.memory_mib").WhoseValue.Should().Be("2048");
        job.Spec.Parameters.Should().ContainKey("batch.ephemeral_gib").WhoseValue.Should().Be("20");
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_WithExplicitResourceRequest_OverridesDerivedProfile()
    {
        // An explicit gp.resource.* request wins over the catalog-derived default.
        var sut = BuildBatchWorkloadService(out _);

        var job = await sut.SubmitJobAsync(CreateValidPlan(), null, CreatePrincipal(), new Dictionary<string, string>
        {
            ["gp.resource.vcpus"] = "8",
            ["gp.resource.ephemeral_gib"] = "150",
        });

        job.Spec.Parameters.Should().ContainKey("batch.vcpus").WhoseValue.Should().Be("8");
        job.Spec.Parameters.Should().ContainKey("batch.ephemeral_gib").WhoseValue.Should().Be("150");
        // Unspecified dimensions still come from the derived default.
        job.Spec.Parameters.Should().ContainKey("batch.memory_mib").WhoseValue.Should().Be("2048");
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_WithLocalDefault_DoesNotCarryBatchSizingParams()
    {
        // The no-remote-workload local baseline must NOT carry batch.* sizing — the keys are
        // meaningless to the local/Kubernetes backend and would break local-runner spec parity.
        _jobStore.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var job = await _sut.SubmitJobAsync(CreateValidPlan(), null, CreatePrincipal());

        job.Spec.Parameters.Should().NotContainKey("batch.vcpus");
        job.Spec.Parameters.Should().NotContainKey("batch.ephemeral_gib");
    }

    private GeoprocessingJobService BuildBatchWorkloadService(out IBatchComputeBackend backend)
    {
        var workloadRegistry = Substitute.For<IExecutionJobDefinitionRegistry>();
        backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("honua-aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        workloadRegistry.ListAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            new ExecutionJobDefinition
            {
                WorkloadId = "geoprocessing-aws-batch",
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.AwsBatch,
                Backend = "honua-aws-batch",
                WorkloadName = "Geoprocessing (AWS Batch)",
                Parameters = new Dictionary<string, string>
                {
                    ["batch.job_queue_arn"] = "arn:aws:batch:us-east-1:123:job-queue/honua-gp",
                    ["batch.region"] = "us-east-1",
                    ["batch.job_definition_arn.s"] = "arn:aws:batch:us-east-1:123:job-definition/honua-gp-s:1",
                    ["batch.job_definition_arn.m"] = "arn:aws:batch:us-east-1:123:job-definition/honua-gp-m:1",
                    ["batch.job_definition_arn.l"] = "arn:aws:batch:us-east-1:123:job-definition/honua-gp-l:1",
                    ["batch.job_definition_arn.xl"] = "arn:aws:batch:us-east-1:123:job-definition/honua-gp-xl:1",
                },
            },
        });
        backend.StartAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>())
            .Returns(new BatchComputeSubmissionResult
            {
                Status = ExecutionJobStatus.Provisioning,
                ProviderOperationId = "job-batch-profile",
                Message = "Submitted to AWS Batch",
            });
        _jobStore.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        return new GeoprocessingJobService(
            _progressStore,
            [_cancellationNotifier],
            _authEvaluator,
            _approvalEvaluator,
            new BuiltInProcessCatalog(),
            NullLogger<GeoprocessingJobService>.Instance,
            DefaultExecutorOptions,
            _jobStore,
            _jobQueue,
            workloadRegistry: workloadRegistry,
            backends: [backend]);
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_WithOnlyLocalWorkload_FallsBackToLocalBackend()
    {
        // Without an activated remote workload (the AWS Batch row gated off / absent),
        // GP must keep routing to the local baseline backend — no regression.
        var workloadRegistry = Substitute.For<IExecutionJobDefinitionRegistry>();
        workloadRegistry.ListAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            new ExecutionJobDefinition
            {
                WorkloadId = "geoprocessing-local",
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "Geoprocessing (local baseline)"
            }
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
            DefaultExecutorOptions,
            _jobStore,
            _jobQueue,
            workloadRegistry: workloadRegistry);

        var job = await sut.SubmitJobAsync(CreateValidPlan(), null, CreatePrincipal());

        job.Spec.Backend.Should().Be("local");
        job.Spec.WorkloadId.Should().Be("geoprocessing-local");
        await _jobQueue.Received().EnqueueAsync(
            job.OperationId,
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

    // -----------------------------------------------------------------------
    // Job ownership scoping (#1576): a coarse Job-level grant must not expose
    // another principal's job state, results, or cancellation.
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}")]
    public async Task GetJob_SubmittedByAnotherPrincipal_ThrowsNotFound()
    {
        var record = CreateOwnedJobRecord("job-1", ExecutionJobStatus.Running, owner: "other-user");
        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>()).Returns(record);

        var act = async () => await _sut.GetJobAsync("job-1", CreatePrincipal());

        // Not-found rather than forbidden so cross-principal probing cannot
        // confirm that the job identifier exists.
        await act.Should().ThrowAsync<GeoprocessingNotFoundException>();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}")]
    public async Task GetJob_SubmittedByCaller_ReturnsRecord()
    {
        var record = CreateOwnedJobRecord("job-1", ExecutionJobStatus.Running, owner: "test-user");
        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>()).Returns(record);

        var result = await _sut.GetJobAsync("job-1", CreatePrincipal());

        result.OperationId.Should().Be("job-1");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}")]
    public async Task GetJob_SubmittedByAnotherPrincipal_AdminRoleCanRead()
    {
        var record = CreateOwnedJobRecord("job-1", ExecutionJobStatus.Running, owner: "other-user");
        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>()).Returns(record);

        var admin = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "ops-admin"), new Claim(ClaimTypes.Role, "admin")], "Test"));

        var result = await _sut.GetJobAsync("job-1", admin);

        result.OperationId.Should().Be("job-1");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}")]
    public async Task GetJob_WithoutRecordedSubmitter_DeniedToNonAdmin()
    {
        // #2753: an ownerless job (no recorded submitter) must NOT be readable by an
        // ordinary Job.Read holder — that was the null-owner read bypass. Surfaced as
        // not-found, matching the cross-principal case.
        var record = CreateJobRecord("job-1", ExecutionJobStatus.Running, owner: null);
        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>()).Returns(record);

        var act = async () => await _sut.GetJobAsync("job-1", CreatePrincipal());

        await act.Should().ThrowAsync<GeoprocessingNotFoundException>();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}")]
    public async Task GetJob_WithoutRecordedSubmitter_AdminCanRead()
    {
        // #2753: admin retains full visibility, including ownerless jobs.
        var record = CreateJobRecord("job-1", ExecutionJobStatus.Running, owner: null);
        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>()).Returns(record);

        var admin = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "ops-admin"), new Claim(ClaimTypes.Role, "admin")], "Test"));

        var result = await _sut.GetJobAsync("job-1", admin);

        result.OperationId.Should().Be("job-1");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs")]
    public async Task ListJobs_NonAdmin_ReturnsOnlyCallersOwnJobs()
    {
        // #2753: a Job.Read holder must see only its OWN jobs. The store page mixes the
        // caller's job, another owner's job, and an ownerless job; only the caller's own
        // job survives the ownership filter (ownerless is admin-only).
        _jobStore.QueryAsync(Arg.Any<ExecutionJobQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ExecutionJobPage
            {
                Items =
                [
                    CreateOwnedJobRecord("job-mine", ExecutionJobStatus.Running, owner: "test-user"),
                    CreateOwnedJobRecord("job-theirs", ExecutionJobStatus.Running, owner: "other-user"),
                    CreateJobRecord("job-ownerless", ExecutionJobStatus.Running, owner: null)
                ]
            });

        var page = await _sut.ListJobsAsync(new GeoprocessingJobListFilter(), CreatePrincipal());

        page.Items.Select(j => j.OperationId).Should().Equal("job-mine");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs")]
    public async Task ListJobs_Admin_SeesAllJobsIncludingOwnerless()
    {
        // #2753: admin retains full visibility across all owners and ownerless jobs.
        _jobStore.QueryAsync(Arg.Any<ExecutionJobQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ExecutionJobPage
            {
                Items =
                [
                    CreateOwnedJobRecord("job-a", ExecutionJobStatus.Running, owner: "test-user"),
                    CreateOwnedJobRecord("job-b", ExecutionJobStatus.Running, owner: "other-user"),
                    CreateJobRecord("job-ownerless", ExecutionJobStatus.Running, owner: null)
                ]
            });

        var admin = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "ops-admin"), new Claim(ClaimTypes.Role, "admin")], "Test"));

        var page = await _sut.ListJobsAsync(new GeoprocessingJobListFilter(), admin);

        page.Items.Select(j => j.OperationId).Should()
            .BeEquivalentTo("job-a", "job-b", "job-ownerless");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs")]
    public async Task ListJobs_NonAdmin_OwnerlessJobExcluded()
    {
        // #2753: the null-owner read bypass — an ownerless job must not leak into a
        // non-admin's listing even when it is the only job.
        _jobStore.QueryAsync(Arg.Any<ExecutionJobQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ExecutionJobPage
            {
                Items = [CreateJobRecord("job-ownerless", ExecutionJobStatus.Running, owner: null)]
            });

        var page = await _sut.ListJobsAsync(new GeoprocessingJobListFilter(), CreatePrincipal());

        page.Items.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/GetJobResult")]
    public async Task GetJobResults_SubmittedByAnotherPrincipal_ThrowsNotFound()
    {
        var record = CreateOwnedJobRecord("job-1", ExecutionJobStatus.Succeeded, owner: "other-user");
        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>()).Returns(record);

        var act = async () => await _sut.GetJobResultsAsync("job-1", CreatePrincipal());

        await act.Should().ThrowAsync<GeoprocessingNotFoundException>();
        await _resultPackageStore.DidNotReceive().GetAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/GetJobResult")]
    public async Task GetJobResults_SubmittedByCaller_ReturnsPackage()
    {
        var record = CreateOwnedJobRecord("job-1", ExecutionJobStatus.Succeeded, owner: "test-user");
        var package = AnalysisResultPackage.CreateCompleted(
            GeoprocessingResultPackageFactory.CreateResultPackageId(record),
            new ResultSummary { Title = "Owned result" },
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
    }

    [UnitTest]
    [Operation(Operations.Delete)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    public async Task CancelJob_SubmittedByAnotherPrincipal_ThrowsNotFound()
    {
        var record = CreateOwnedJobRecord("job-1", ExecutionJobStatus.Running, owner: "other-user");
        _jobStore.GetAsync("job-1", Arg.Any<CancellationToken>()).Returns(record);

        var act = async () => await _sut.CancelJobAsync("job-1", CreatePrincipal());

        await act.Should().ThrowAsync<GeoprocessingNotFoundException>();
        _cancellationNotifier.DidNotReceive().Cancel(Arg.Any<string>());
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/GetJobResult")]
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
    [Endpoint("POST /geospatial.v1.ProcessService/GetJobResult")]
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
    [Endpoint("POST /geospatial.v1.ProcessService/GetJobResult")]
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
            DefaultExecutorOptions,
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
                j.ErrorMessage == "Submission failed."),
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
            DefaultExecutorOptions,
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
            DefaultExecutorOptions,
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
            DefaultExecutorOptions,
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
            DefaultExecutorOptions,
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
            DefaultExecutorOptions,
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
            DefaultExecutorOptions,
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
            DefaultExecutorOptions,
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
            DefaultExecutorOptions,
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
            DefaultExecutorOptions,
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
            DefaultExecutorOptions,
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
            DefaultExecutorOptions,
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
            DefaultExecutorOptions,
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
            DefaultExecutorOptions,
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
            DefaultExecutorOptions,
            _jobStore,
            workloadRegistry: workloadRegistry,
            backends: [backend]);

        var act = async () => await sut.SubmitJobAsync(CreateValidPlan(), null, CreatePrincipal());

        await act.Should().ThrowAsync<InvalidOperationException>();

        await _jobStore.Received().TrySetAsync(
            Arg.Is<ExecutionJobRecord>(j =>
                j.Status == ExecutionJobStatus.Failed &&
                j.ErrorMessage == "Submission failed."),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_BackendBelowRequiredContractVersion_FailsWithoutDispatch()
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
                // This workload's jobs require job-contract v2 ...
                ContractVersion = 2
            }
        });

        // ... but the target backend's workers only run up to v1 (a mid-rolling-upgrade worker).
        backend.GetCapabilitiesAsync(Arg.Any<CancellationToken>())
            .Returns(new BatchComputeBackendCapabilities { MaxSupportedContractVersion = 1 });

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
            DefaultExecutorOptions,
            _jobStore,
            workloadRegistry: workloadRegistry,
            backends: [backend]);

        await sut.SubmitJobAsync(CreateValidPlan(), null, CreatePrincipal());

        // The job never reaches the worker: the version gate fails it before dispatch.
        await backend.DidNotReceive().StartAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>());

        await _jobStore.Received().TrySetAsync(
            Arg.Is<ExecutionJobRecord>(j =>
                j.Status == ExecutionJobStatus.Failed &&
                j.ErrorMessage != null &&
                j.ErrorMessage.Contains("job-contract version")),
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
            DefaultExecutorOptions,
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
            DefaultExecutorOptions,
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
            DefaultExecutorOptions,
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
            DefaultExecutorOptions,
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
            DefaultExecutorOptions,
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
            DefaultExecutorOptions,
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
            DefaultExecutorOptions,
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
            DefaultExecutorOptions,
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
            DefaultExecutorOptions,
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

    private static readonly IOptionsMonitor<GeoprocessingExecutorOptions> DefaultExecutorOptions =
        new StaticOptionsMonitor<GeoprocessingExecutorOptions>(new GeoprocessingExecutorOptions());

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

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

    private static AnalysisPlan CreateSinkPlan() => new()
    {
        PlanId = "plan-sink",
        IntentId = "intent-sink",
        Steps =
        [
            new AnalysisPlanStep
            {
                StepId = "step-sink",
                Kind = AnalysisPlanStepKind.Geoprocess,
                ProcessId = "sink.honua-layer",
                Inputs = new Dictionary<string, string>
                {
                    // Minimal canonical geo+json data URI ({"type":"FeatureCollection","features":[]}).
                    ["input"] =
                        "data:application/geo+json;base64," +
                        "eyJ0eXBlIjoiRmVhdHVyZUNvbGxlY3Rpb24iLCJmZWF0dXJlcyI6W119",
                    ["layer"] = "sink_layer",
                    ["targetSrid"] = "4326"
                }
            }
        ]
    };

    // Jobs default to being owned by the "test-user" principal that CreatePrincipal()
    // returns, so ownership-scoped operations (get/results/cancel) succeed for the
    // conventional caller. Pass owner: null to exercise the ownerless (admin-only) path.
    private static ExecutionJobRecord CreateJobRecord(
        string jobId,
        ExecutionJobStatus status,
        string backend = LocalBatchComputeBackend.BackendId,
        BatchComputeTargetKind targetKind = BatchComputeTargetKind.KubernetesJob,
        string? owner = "test-user") => new()
        {
            OperationId = jobId,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Audit = new OperationAuditInfo { RequestedBy = owner },
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = targetKind,
                Backend = backend,
                WorkloadName = "test-workload"
            }
        };

    private static ExecutionJobRecord CreateOwnedJobRecord(
        string jobId,
        ExecutionJobStatus status,
        string owner)
        => CreateJobRecord(jobId, status) with
        {
            Audit = new OperationAuditInfo { RequestedBy = owner }
        };

    private static ClaimsPrincipal CreatePrincipal()
        => new(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "test-user")], "Test"));
}
