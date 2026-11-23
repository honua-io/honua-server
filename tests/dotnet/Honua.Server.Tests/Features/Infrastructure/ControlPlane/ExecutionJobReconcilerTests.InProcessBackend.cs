// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Infrastructure.Tiles;
using Honua.Server.Features.Admin.Routing;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

// In-process job producers must build specs that resolve to the backend the host registers
// (BatchAndDeployBackendsRegistration), not to a test double shaped after the spec.
public sealed partial class ExecutionJobReconcilerTests
{
    [UnitTest]
    public void Resolve_TileExportSpec_ResolvesRegisteredInProcessBackend()
    {
        var local = CreateRegisteredLocalBackend();

        var spec = TileExportExecutionSpecBuilder.Build(CreateTileExportPlan());

        RegisteredBackends(local).Resolve(spec.Backend, spec.TargetKind).Should().BeSameAs(local);
    }

    [UnitTest]
    public void Resolve_TopologyRebuildSpec_ResolvesRegisteredInProcessBackend()
    {
        var local = CreateRegisteredLocalBackend();

        var spec = CreateTopologyRebuildSpec();

        RegisteredBackends(local).Resolve(spec.Backend, spec.TargetKind).Should().BeSameAs(local);
    }

    [UnitTest]
    public void Resolve_NamedNonLocalBackendWithOtherTargetKind_ReturnsNull()
    {
        var pool = Substitute.For<IBatchComputeBackend>();
        pool.BackendName.Returns(LocalProcessPoolBatchComputeBackend.AdapterBackendName);
        pool.TargetKind.Returns(BatchComputeTargetKind.LocalProcess);

        var resolved = RegisteredBackends(CreateRegisteredLocalBackend(), pool)
            .Resolve(LocalProcessPoolBatchComputeBackend.AdapterBackendName, BatchComputeTargetKind.KubernetesJob);

        resolved.Should().BeNull();
    }

    [UnitTest]
    public void Resolve_LocalBackendWithExactTargetKind_PrefersExactMatch()
    {
        var exact = Substitute.For<IBatchComputeBackend>();
        exact.BackendName.Returns(LocalBatchComputeBackend.BackendId);
        exact.TargetKind.Returns(BatchComputeTargetKind.LocalProcess);

        var resolved = RegisteredBackends(CreateRegisteredLocalBackend(), exact)
            .Resolve(LocalBatchComputeBackend.BackendId, BatchComputeTargetKind.LocalProcess);

        resolved.Should().BeSameAs(exact);
    }

    [UnitTest]
    public async Task ReconcileExecutionJob_PersistedTopologyRebuildRow_IsNotFailedAsBackendMissing()
    {
        var job = CreateJobRecord("topology-queued", ExecutionJobStatus.Queued, "local", BatchComputeTargetKind.LocalProcess)
            with
        { Spec = CreateTopologyRebuildSpec() };
        var store = new InMemoryExecutionJobStore(job);
        var sut = new ExecutionJobReconciler(store, RegisteredBackends(CreateRegisteredLocalBackend()),
            new InMemoryProgressStore(), NullLogger<ExecutionJobReconciler>.Instance);

        await sut.ReconcileExecutionJobAsync(job.OperationId);

        var stored = (await store.GetAsync(job.OperationId))!;
        stored.Status.Should().Be(ExecutionJobStatus.Queued);
        stored.ErrorMessage.Should().BeNull();
    }

    private static LocalBatchComputeBackend CreateRegisteredLocalBackend()
        => new(new InMemoryProgressStore(), []);

    private static IBatchComputeBackend[] RegisteredBackends(params IBatchComputeBackend[] backends) => backends;

    private static ExecutionJobSpec CreateTopologyRebuildSpec()
        => NetworkTopologyRebuildExecutionSpecBuilder.Build(new NetworkTopologyRebuildJobRequest("roads", 3, 1, 7, 4326));

    private static TileExportJobPlan CreateTileExportPlan()
        => new()
        {
            SourceKind = TileExportSourceKind.Map,
            ResourceId = "basemap",
            Source = new TileExportMapSourceDescriptor(
                42,
                [new("0", "default", 1)],
                TileExportMapPublicationPolicy.Watermark(42),
                null),
            ZoomLevels = [0, 1],
            West = -180,
            South = -85,
            East = 180,
            North = 85,
            TileImageFormat = "PNG",
            PackageFormat = TileExportPackageFormat.Tpkx,
            MaxTiles = 100,
            MaxArtifactBytes = 1024 * 1024,
            RetentionSeconds = 3600
        };
}
