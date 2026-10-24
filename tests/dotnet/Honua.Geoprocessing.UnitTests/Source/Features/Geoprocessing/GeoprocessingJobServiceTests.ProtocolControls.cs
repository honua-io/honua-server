// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing;

public sealed partial class GeoprocessingJobServiceTests
{
    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_SameKeyDifferentWorkspace_ConflictsAndExactRetryReturnsOriginal()
    {
        var store = new InMemoryExecutionJobStore();
        var service = CreateStoreBackedService(store);
        var plan = CreateValidPlan();
        var principal = CreateStablePrincipal();
        var alpha = new Dictionary<string, string>
        {
            [GeoprocessingProtocolMetadataKeys.GPServerWorkspace] = "alpha"
        };
        var beta = new Dictionary<string, string>
        {
            [GeoprocessingProtocolMetadataKeys.GPServerWorkspace] = "beta"
        };

        var first = await service.SubmitJobAsync(plan, "workspace-key", principal, alpha);
        var replay = await service.SubmitJobAsync(plan, "workspace-key", principal, new Dictionary<string, string>
        {
            [GeoprocessingProtocolMetadataKeys.GPServerWorkspace] = "alpha"
        });

        replay.OperationId.Should().Be(first.OperationId);
        first.Audit.RequestFingerprint.Should().Be(GeoprocessingJobService.CreateRequestFingerprint(plan));
        await FluentActions.Awaiting(() => service.SubmitJobAsync(plan, "workspace-key", principal, beta))
            .Should().ThrowAsync<GeoprocessingIdempotencyConflictException>();
    }

    [UnitTheory]
    [InlineData(GeoprocessingProtocolMetadataKeys.GPServerOverwriteOutput, "false", "true")]
    [InlineData(GeoprocessingProtocolMetadataKeys.GPServerOutSr, "4326", "3857")]
    [InlineData(GeoprocessingProtocolMetadataKeys.OutputNamePrefix + "0", "outputFeatureLayer", "outputTable")]
    [Operation(Operations.Create)]
    public async Task SubmitJob_SameKeyDifferentOutputControl_ConflictsAndExactRetryReturnsOriginal(
        string key, string original, string changed)
    {
        var store = new InMemoryExecutionJobStore();
        var service = CreateStoreBackedService(store);
        var plan = CreateValidPlan();
        var principal = CreateStablePrincipal();

        var first = await service.SubmitJobAsync(plan, "control-key", principal, new Dictionary<string, string>
        {
            [key] = original
        });
        var replay = await service.SubmitJobAsync(plan, "control-key", principal, new Dictionary<string, string>
        {
            [key] = original
        });

        replay.OperationId.Should().Be(first.OperationId);
        await FluentActions.Awaiting(() => service.SubmitJobAsync(plan, "control-key", principal, new Dictionary<string, string>
        {
            [key] = changed
        })).Should().ThrowAsync<GeoprocessingIdempotencyConflictException>();
    }

    [UnitTheory]
    [InlineData(GeoprocessingProtocolMetadataKeys.GPServerWorkspace, "alpha", "operator", false)]
    [InlineData(GeoprocessingProtocolMetadataKeys.GPServerWorkspace, "alpha", "operator", true)]
    [InlineData(GeoprocessingProtocolMetadataKeys.GPServerOverwriteOutput, "false", "true", false)]
    [InlineData(GeoprocessingProtocolMetadataKeys.GPServerOverwriteOutput, "false", "true", true)]
    [InlineData(GeoprocessingProtocolMetadataKeys.GPServerOutSr, "4326", "3857", false)]
    [InlineData(GeoprocessingProtocolMetadataKeys.GPServerOutSr, "4326", "3857", true)]
    [InlineData(GeoprocessingProtocolMetadataKeys.OutputNamePrefix + "0", "requested", "configured", false)]
    [InlineData(GeoprocessingProtocolMetadataKeys.OutputNamePrefix + "0", "requested", "configured", true)]
    [Operation(Operations.Create)]
    public async Task SubmitJob_WorkloadControlDefaultsAndOverrides_ExactRetryReplaysDurableJob(
        string key, string requested, string configured, bool omitRequestControl)
    {
        var store = new InMemoryExecutionJobStore();
        var workloadParameters = new Dictionary<string, string> { [key] = configured };
        var registry = Substitute.For<IExecutionJobDefinitionRegistry>();
        registry.ListAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            new ExecutionJobDefinition
            {
                WorkloadId = "geoprocessing-local",
                WorkloadName = "Geoprocessing (local)",
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                Parameters = workloadParameters
            }
        });
        var metadata = omitRequestControl ? null : new Dictionary<string, string> { [key] = requested };
        var plan = CreateValidPlan();
        var principal = CreateStablePrincipal();
        var service = CreateStoreBackedService(store, registry);
        var first = await service.SubmitJobAsync(plan, "configured-control-key", principal, metadata);
        first.Spec.Parameters[key].Should().Be(configured);
        first.Audit.RequestFingerprint.Should().Be(GeoprocessingJobService.CreateRequestFingerprint(plan));

        // Recreate the service so replay must use the persisted spec, not instance-local state.
        var restartedService = CreateStoreBackedService(store, registry);
        var replay = await restartedService.SubmitJobAsync(plan, "configured-control-key", principal, metadata);
        replay.OperationId.Should().Be(first.OperationId);

        // A changed effective control still conflicts; normalizing defaults must not ignore them.
        workloadParameters[key] = requested;
        await FluentActions.Awaiting(() => restartedService.SubmitJobAsync(
                plan, "configured-control-key", principal, metadata))
            .Should().ThrowAsync<GeoprocessingIdempotencyConflictException>();
    }

    private GeoprocessingJobService CreateStoreBackedService(
        InMemoryExecutionJobStore store,
        IExecutionJobDefinitionRegistry? workloadRegistry = null,
        IGeoprocessingHostRuntime? hostRuntime = null)
        => new(
            _progressStore,
            [_cancellationNotifier],
            _authEvaluator,
            _approvalEvaluator,
            new BuiltInProcessCatalog(),
            NullLogger<GeoprocessingJobService>.Instance,
            DefaultExecutorOptions,
            store,
            _jobQueue,
            workloadRegistry: workloadRegistry,
            resultPackageStore: _resultPackageStore,
            hostRuntime: hostRuntime);
}
