// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Geoprocessing.Execution;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>
/// Unit coverage for the dispatcher that routes claimed geoprocessing jobs
/// to the correct per-process executor. The dispatcher is the single
/// IJobExecutor registered for ExecutionJobKind.Geoprocessing after slice 2;
/// slice 3 extended it with geometry.area + geometry.union; slice 4 adds
/// geometry.centroid + geometry.length + geometry.convex-hull. This test
/// pins the routing contract so unknown process ids never reach a
/// per-process executor by accident.
/// </summary>
public sealed class GeoprocessingDispatchJobExecutorTests
{
    [UnitTest]
    public async Task ExecuteAsync_UnknownProcessId_FailsWithSupportedSet()
    {
        var dispatcher = CreateDispatcher();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-unknown");

        var record = CreateJobRecord("geometry.simplify");

        var result = await dispatcher.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("geometry.simplify");
        result.ErrorMessage.Should().Contain("geometry.buffer");
        result.ErrorMessage.Should().Contain("geometry.clip");
        result.ErrorMessage.Should().Contain("geometry.intersect");
        result.ErrorMessage.Should().Contain("geometry.project");
        result.ErrorMessage.Should().Contain("geometry.area");
        result.ErrorMessage.Should().Contain("geometry.union");
        result.ErrorMessage.Should().Contain("geometry.centroid");
        result.ErrorMessage.Should().Contain("geometry.length");
        result.ErrorMessage.Should().Contain("geometry.convex-hull");
    }

    [UnitTest]
    public async Task ExecuteAsync_MissingProcessId_FailsCleanly()
    {
        var dispatcher = CreateDispatcher();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-missing");

        var record = CreateJobRecord(processId: null);

        var result = await dispatcher.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("<none>");
    }

    private static readonly string[] SliceFourProcessIds =
    {
        "geometry.buffer",
        "geometry.clip",
        "geometry.intersect",
        "geometry.project",
        "geometry.area",
        "geometry.union",
        "geometry.centroid",
        "geometry.length",
        "geometry.convex-hull",
    };

    [UnitTest]
    public void SupportedProcessIds_ListsSliceFourExecutors()
    {
        var dispatcher = CreateDispatcher();
        dispatcher.SupportedProcessIds.Should().BeEquivalentTo(SliceFourProcessIds);
    }

    [UnitTest]
    public void Kind_IsGeoprocessing()
    {
        var dispatcher = CreateDispatcher();
        dispatcher.Kind.Should().Be(ExecutionJobKind.Geoprocessing);
    }

    private static GeoprocessingDispatchJobExecutor CreateDispatcher()
    {
        var options = new GeoprocessingExecutorOptions
        {
            MaxArtifactBytes = 50L * 1024L * 1024L,
            ResultRetention = TimeSpan.FromDays(7)
        };
        var monitor = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        monitor.CurrentValue.Returns(options);

        return new GeoprocessingDispatchJobExecutor(
            new GeometryBufferJobExecutor(monitor, NullLogger<GeometryBufferJobExecutor>.Instance),
            new GeometryClipJobExecutor(monitor, NullLogger<GeometryClipJobExecutor>.Instance),
            new GeometryIntersectJobExecutor(monitor, NullLogger<GeometryIntersectJobExecutor>.Instance),
            new GeometryProjectJobExecutor(monitor, NullLogger<GeometryProjectJobExecutor>.Instance),
            new GeometryAreaJobExecutor(monitor, NullLogger<GeometryAreaJobExecutor>.Instance),
            new GeometryUnionJobExecutor(monitor, NullLogger<GeometryUnionJobExecutor>.Instance),
            new GeometryCentroidJobExecutor(monitor, NullLogger<GeometryCentroidJobExecutor>.Instance),
            new GeometryLengthJobExecutor(monitor, NullLogger<GeometryLengthJobExecutor>.Instance),
            new GeometryConvexHullJobExecutor(monitor, NullLogger<GeometryConvexHullJobExecutor>.Instance),
            NullLogger<GeoprocessingDispatchJobExecutor>.Instance);
    }

    private static ExecutionJobRecord CreateJobRecord(string? processId)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(processId))
        {
            parameters[ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = processId;
            parameters["protocolProcessId"] = processId;
        }

        return new ExecutionJobRecord
        {
            OperationId = "op-test",
            Status = ExecutionJobStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "geoprocessing:test",
                Parameters = parameters
            }
        };
    }
}
