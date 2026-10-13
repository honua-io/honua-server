// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

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

    private GeoprocessingJobService CreateStoreBackedService(InMemoryExecutionJobStore store)
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
            resultPackageStore: _resultPackageStore);
}
