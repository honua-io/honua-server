// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.ControlPlane;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>
/// Unit coverage for the dispatcher that routes claimed geoprocessing jobs
/// to the correct per-process executor. The dispatcher is the single
/// IJobExecutor registered for ExecutionJobKind.Geoprocessing after slice 2;
/// slice 3 extended it with geometry.area + geometry.union; slice 4 added
/// geometry.centroid + geometry.length + geometry.convex-hull; slice 5 adds
/// geometry.dissolve + geometry.simplify + geometry.snap. This test pins
/// the routing contract so unknown process ids never reach a per-process
/// executor by accident.
/// </summary>
public sealed class GeoprocessingDispatchJobExecutorTests
{
    [UnitTest]
    public async Task ExecuteAsync_UnknownProcessId_FailsWithSupportedSet()
    {
        var dispatcher = CreateDispatcher();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-unknown");

        // analytics.cluster is the bare-id catalog process that is NOT job-routed (it
        // executes through the layer-scoped PostGIS analytics protocol path, not the
        // dispatcher) — pick it for the unknown-id smoke. The managed counterpart
        // analytics.cluster-managed IS job-routed (#1260) and so cannot be used here.
        var record = CreateJobRecord("analytics.cluster");

        var result = await dispatcher.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("analytics.cluster");
        result.ErrorMessage.Should().Contain("geometry.buffer");
        result.ErrorMessage.Should().Contain("geometry.clip");
        result.ErrorMessage.Should().Contain("geometry.intersect");
        result.ErrorMessage.Should().Contain("geometry.project");
        result.ErrorMessage.Should().Contain("geometry.area");
        result.ErrorMessage.Should().Contain("geometry.union");
        result.ErrorMessage.Should().Contain("geometry.centroid");
        result.ErrorMessage.Should().Contain("geometry.length");
        result.ErrorMessage.Should().Contain("geometry.convex-hull");
        result.ErrorMessage.Should().Contain("geometry.dissolve");
        result.ErrorMessage.Should().Contain("geometry.simplify");
        result.ErrorMessage.Should().Contain("geometry.snap");
        result.ErrorMessage.Should().Contain("geometry.make-valid");
        result.ErrorMessage.Should().Contain("geometry.difference");
        result.ErrorMessage.Should().Contain("analytics.spatial-join-managed");
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

    private static readonly string[] SliceFiveProcessIds =
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
        "geometry.dissolve",
        "geometry.simplify",
        "geometry.snap",
        "geometry.make-valid",
        "geometry.difference",
        "analytics.spatial-join-managed",
        "analytics.cluster-managed",
        "analytics.buffer-aggregate-managed",
        "analytics.density-managed",
        "transform.attribute-rename",
        "transform.attribute-cast",
        "transform.computed-field",
        "transform.attribute-filter",
        "transform.attribute-join",
        "transform.aggregate",
        "transform.pivot",
        "transform.unpivot",
        "transform.spatial-filter",
        "transform.clip",
        "transform.dedup",
        "transform.reproject",
        "source.geojson",
        "source.csv",
        "sink.geojson-file",
        "sink.quarantine",
        "sink.external-postgis",
        "import.dataset",
    };

    [UnitTest]
    public void SupportedProcessIds_ListsSliceFiveExecutors()
    {
        var dispatcher = CreateDispatcher();
        dispatcher.SupportedProcessIds.Should().BeEquivalentTo(SliceFiveProcessIds);
    }

    [UnitTest]
    public void Kind_IsGeoprocessing()
    {
        var dispatcher = CreateDispatcher();
        dispatcher.Kind.Should().Be(ExecutionJobKind.Geoprocessing);
    }

    // -----------------------------------------------------------------------
    // env:workspace / env:overwriteOutput routing (GPServer submitJob/execute)
    // -----------------------------------------------------------------------

    [UnitTest]
    public async Task ExecuteAsync_NoWorkspaceRequested_PublishesArtifactWithoutTouchingWorkspaceService()
    {
        var workspaceLifecycle = Substitute.For<IWorkspaceLifecycleService>();
        var dispatcher = CreateFakeExecutorDispatcher(BuildScopeFactory(workspaceLifecycle));
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-no-workspace");

        var record = CreateFakeExecutorJobRecord(workspaceId: null, overwriteOutput: null);

        var result = await dispatcher.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        await context.Received(1).PublishArtifactAsync("data:fake-artifact", Arg.Any<CancellationToken>());
        await workspaceLifecycle.DidNotReceiveWithAnyArgs().AddOrReplaceArtifactAsync(
            default!, default, default!, default, cancellationToken: default);
    }

    [UnitTest]
    public async Task ExecuteAsync_WorkspaceRequested_RoutesArtifactThroughWorkspaceLifecycle()
    {
        var workspaceLifecycle = Substitute.For<IWorkspaceLifecycleService>();
        workspaceLifecycle
            .AddOrReplaceArtifactAsync(
                "ws-1", ArtifactKind.File, "artifact1", false,
                uri: "data:fake-artifact", cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new Artifact
            {
                ArtifactId = "art-1",
                Kind = ArtifactKind.File,
                Label = "artifact1",
                State = ArtifactLifecycleState.Available,
                CreatedAt = DateTimeOffset.UtcNow,
                WorkspaceId = "ws-1"
            });

        var dispatcher = CreateFakeExecutorDispatcher(BuildScopeFactory(workspaceLifecycle));
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-workspace");

        var record = CreateFakeExecutorJobRecord(workspaceId: "ws-1", overwriteOutput: null);

        var result = await dispatcher.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        await workspaceLifecycle.Received(1).AddOrReplaceArtifactAsync(
            "ws-1", ArtifactKind.File, "artifact1", false,
            uri: "data:fake-artifact", cancellationToken: Arg.Any<CancellationToken>());
        // The workspace ledger write does not replace the durable job-record publish.
        await context.Received(1).PublishArtifactAsync("data:fake-artifact", Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task ExecuteAsync_WorkspaceCollisionWithoutOverwrite_FailsWithClearMessage()
    {
        var workspaceLifecycle = Substitute.For<IWorkspaceLifecycleService>();
        workspaceLifecycle
            .AddOrReplaceArtifactAsync(
                "ws-1", ArtifactKind.File, "artifact1", false,
                uri: Arg.Any<string>(), cancellationToken: Arg.Any<CancellationToken>())
            .ThrowsAsync(new ArtifactAlreadyExistsException("ws-1", "artifact1"));

        var dispatcher = CreateFakeExecutorDispatcher(BuildScopeFactory(workspaceLifecycle));
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-collision");

        var record = CreateFakeExecutorJobRecord(workspaceId: "ws-1", overwriteOutput: null);

        var result = await dispatcher.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("artifact1");
        result.ErrorMessage.Should().Contain("overwriteOutput");
        // The collision is caught before the durable job-record publish happens.
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_WorkspaceCollisionWithOverwrite_SucceedsAndReplaces()
    {
        var workspaceLifecycle = Substitute.For<IWorkspaceLifecycleService>();
        workspaceLifecycle
            .AddOrReplaceArtifactAsync(
                "ws-1", ArtifactKind.File, "artifact1", true,
                uri: Arg.Any<string>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new Artifact
            {
                ArtifactId = "art-2",
                Kind = ArtifactKind.File,
                Label = "artifact1",
                State = ArtifactLifecycleState.Available,
                CreatedAt = DateTimeOffset.UtcNow,
                WorkspaceId = "ws-1"
            });

        var dispatcher = CreateFakeExecutorDispatcher(BuildScopeFactory(workspaceLifecycle));
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-overwrite");

        var record = CreateFakeExecutorJobRecord(workspaceId: "ws-1", overwriteOutput: true);

        var result = await dispatcher.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        await workspaceLifecycle.Received(1).AddOrReplaceArtifactAsync(
            "ws-1", ArtifactKind.File, "artifact1", true,
            uri: Arg.Any<string>(), cancellationToken: Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task ExecuteAsync_WorkspaceRequestedWithNoProviderConfigured_FailsFastWithoutRunningHandler()
    {
        // No IServiceScopeFactory at all: the dispatcher itself was constructed
        // without one (mirrors positional test construction / hosts with no
        // workspace storage provider registered).
        var dispatcher = CreateFakeExecutorDispatcher(scopeFactory: null);
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-no-provider");

        var record = CreateFakeExecutorJobRecord(workspaceId: "ws-1", overwriteOutput: null);

        var result = await dispatcher.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("ws-1");
        result.ErrorMessage.Should().Contain("no workspace storage provider");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    private static IServiceScopeFactory BuildScopeFactory(IWorkspaceLifecycleService workspaceLifecycle)
    {
        var services = new ServiceCollection();
        services.AddSingleton(workspaceLifecycle);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    /// <summary>
    /// Job record routed to <see cref="FakeArtifactPublishingExecutor"/> (registered
    /// only in the dispatchers these workspace-routing tests build), isolating the
    /// workspace-routing behavior from real geometry executor logic.
    /// </summary>
    private static ExecutionJobRecord CreateFakeExecutorJobRecord(string? workspaceId, bool? overwriteOutput)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = FakeArtifactPublishingExecutor.HandledProcessId,
            ["protocolProcessId"] = FakeArtifactPublishingExecutor.HandledProcessId,
            [$"{GeoprocessingProtocolMetadataKeys.OutputNamePrefix}0"] = "artifact1"
        };

        if (workspaceId is not null)
        {
            parameters[GeoprocessingProtocolMetadataKeys.GPServerWorkspace] = workspaceId;
        }

        if (overwriteOutput is { } overwrite)
        {
            parameters[GeoprocessingProtocolMetadataKeys.GPServerOverwriteOutput] = overwrite ? "true" : "false";
        }

        return new ExecutionJobRecord
        {
            OperationId = "op-fake",
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

    /// <summary>
    /// Minimal <see cref="IProcessExecutor"/> that publishes a single fixed
    /// artifact and succeeds, used to isolate workspace-routing tests from real
    /// geometry executor logic.
    /// </summary>
    private sealed class FakeArtifactPublishingExecutor : IProcessExecutor
    {
        public const string HandledProcessId = "test.fake-artifact-publisher";

        public IReadOnlySet<string> ProcessIds { get; } = new HashSet<string> { HandledProcessId };

        public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

        public async Task<JobExecutionResult> ExecuteAsync(
            ExecutionJobRecord job, IJobExecutionContext context, CancellationToken cancellationToken)
        {
            await context.PublishArtifactAsync("data:fake-artifact", cancellationToken).ConfigureAwait(false);
            return JobExecutionResult.Succeeded();
        }
    }

    /// <summary>
    /// Builds a dispatcher routing only <see cref="FakeArtifactPublishingExecutor"/>,
    /// isolated from <see cref="CreateDispatcher"/>'s full executor set so the
    /// exact-match <see cref="SupportedProcessIds_ListsSliceFiveExecutors"/>
    /// assertion is unaffected by these workspace-routing tests.
    /// </summary>
    private static GeoprocessingDispatchJobExecutor CreateFakeExecutorDispatcher(
        IServiceScopeFactory? scopeFactory)
    {
        IProcessExecutor[] executors = { new FakeArtifactPublishingExecutor() };
        return new GeoprocessingDispatchJobExecutor(
            executors,
            NullLogger<GeoprocessingDispatchJobExecutor>.Instance,
            usageTelemetry: null,
            serviceScopeFactory: scopeFactory);
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

        // Auto-registration contract (#2122): the dispatcher routes by enumerating
        // the IProcessExecutor set and keying on each executor's self-declared
        // ProcessIds, so the test composes the same executor instances as a flat
        // list instead of the former positional constructor.
        IProcessExecutor[] executors =
        {
            new GeometryBufferJobExecutor(monitor, NullLogger<GeometryBufferJobExecutor>.Instance),
            new GeometryClipJobExecutor(monitor, NullLogger<GeometryClipJobExecutor>.Instance),
            new GeometryIntersectJobExecutor(monitor, NullLogger<GeometryIntersectJobExecutor>.Instance),
            new GeometryProjectJobExecutor(monitor, NullLogger<GeometryProjectJobExecutor>.Instance),
            new GeometryAreaJobExecutor(monitor, NullLogger<GeometryAreaJobExecutor>.Instance),
            new GeometryUnionJobExecutor(monitor, NullLogger<GeometryUnionJobExecutor>.Instance),
            new GeometryCentroidJobExecutor(monitor, NullLogger<GeometryCentroidJobExecutor>.Instance),
            new GeometryLengthJobExecutor(monitor, NullLogger<GeometryLengthJobExecutor>.Instance),
            new GeometryConvexHullJobExecutor(monitor, NullLogger<GeometryConvexHullJobExecutor>.Instance),
            new GeometryDissolveJobExecutor(monitor, NullLogger<GeometryDissolveJobExecutor>.Instance),
            new GeometrySimplifyJobExecutor(monitor, NullLogger<GeometrySimplifyJobExecutor>.Instance),
            new GeometrySnapJobExecutor(monitor, NullLogger<GeometrySnapJobExecutor>.Instance),
            new GeometryMakeValidJobExecutor(monitor, NullLogger<GeometryMakeValidJobExecutor>.Instance),
            new GeometryDifferenceJobExecutor(monitor, NullLogger<GeometryDifferenceJobExecutor>.Instance),
            new ManagedSpatialJoinExecutor(monitor),
            new ManagedClusterExecutor(monitor),
            new ManagedBufferAggregateExecutor(monitor),
            new ManagedDensityExecutor(monitor),
            new AttributeRenameTransformExecutor(monitor),
            new AttributeCastTransformExecutor(monitor),
            new ComputedFieldTransformExecutor(monitor),
            new AttributeFilterTransformExecutor(monitor),
            new AttributeJoinTransformExecutor(monitor),
            new AggregateTransformExecutor(monitor),
            new PivotTransformExecutor(monitor),
            new UnpivotTransformExecutor(monitor),
            new SpatialFilterTransformExecutor(monitor),
            new ClipTransformExecutor(monitor),
            new DedupTransformExecutor(monitor),
            new ReprojectTransformExecutor(monitor),
            new GeoJsonSourceExecutor(monitor),
            new CsvSourceExecutor(monitor),
            new GeoJsonFileSinkExecutor(monitor),
            new QuarantineSinkExecutor(monitor),
            new ExternalPostgisSinkExecutor(monitor),
            new ImportDatasetJobExecutor(
                Substitute.For<IServiceScopeFactory>(),
                NullLogger<ImportDatasetJobExecutor>.Instance,
                Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>()),
        };

        return new GeoprocessingDispatchJobExecutor(
            executors,
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
