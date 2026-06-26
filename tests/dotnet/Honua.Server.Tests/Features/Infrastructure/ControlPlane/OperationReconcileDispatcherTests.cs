// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Phase 0 dispatcher-seam tests: the dispatcher routes each operation kind to the matching typed
/// reconciler's reconcile-once method and does nothing else (no extra coordination, no behavior).
/// </summary>
public sealed class OperationReconcileDispatcherTests
{
    [Fact]
    public async Task ReconcileOnceAsync_ExecutionJob_RoutesToExecutionJobReconciler()
    {
        var deploy = Substitute.For<IWorkflowOperationReconciler>();
        var execution = Substitute.For<IExecutionJobReconciler>();
        var metadata = Substitute.For<IMetadataReleaseOperationReconciler>();
        var coordinated = Substitute.For<ICoordinatedReleaseReconciler>();
        var sut = new OperationReconcileDispatcher(deploy, execution, metadata, coordinated);

        await sut.ReconcileOnceAsync(new OperationRef(OperationKind.ExecutionJob, "job-1"));

        await execution.Received(1).ReconcileExecutionJobAsync("job-1", Arg.Any<CancellationToken>());
        await deploy.DidNotReceiveWithAnyArgs().ReconcileWorkflowOperationAsync(default!, default);
        await metadata.DidNotReceiveWithAnyArgs().ReconcileMetadataReleaseAsync(default!, default);
        await coordinated.DidNotReceiveWithAnyArgs().ReconcileCoordinatedReleaseAsync(default!, default);
    }

    [Fact]
    public async Task ReconcileOnceAsync_DeployWorkflow_RoutesToDeployReconciler()
    {
        var deploy = Substitute.For<IWorkflowOperationReconciler>();
        var sut = new OperationReconcileDispatcher(
            deploy,
            Substitute.For<IExecutionJobReconciler>(),
            Substitute.For<IMetadataReleaseOperationReconciler>(),
            Substitute.For<ICoordinatedReleaseReconciler>());

        await sut.ReconcileOnceAsync(new OperationRef(OperationKind.DeployWorkflow, "deploy-1"));

        await deploy.Received(1).ReconcileWorkflowOperationAsync("deploy-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileOnceAsync_MetadataRelease_RoutesToMetadataReleaseReconciler()
    {
        var metadata = Substitute.For<IMetadataReleaseOperationReconciler>();
        var sut = new OperationReconcileDispatcher(
            Substitute.For<IWorkflowOperationReconciler>(),
            Substitute.For<IExecutionJobReconciler>(),
            metadata,
            Substitute.For<ICoordinatedReleaseReconciler>());

        await sut.ReconcileOnceAsync(new OperationRef(OperationKind.MetadataRelease, "mr-1"));

        await metadata.Received(1).ReconcileMetadataReleaseAsync("mr-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileOnceAsync_CoordinatedRelease_RoutesToCoordinatedReconciler()
    {
        var coordinated = Substitute.For<ICoordinatedReleaseReconciler>();
        var sut = new OperationReconcileDispatcher(
            Substitute.For<IWorkflowOperationReconciler>(),
            Substitute.For<IExecutionJobReconciler>(),
            Substitute.For<IMetadataReleaseOperationReconciler>(),
            coordinated);

        await sut.ReconcileOnceAsync(new OperationRef(OperationKind.CoordinatedRelease, "cr-1"));

        await coordinated.Received(1).ReconcileCoordinatedReleaseAsync("cr-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileOnceAsync_EmptyOperationId_IsNoOp()
    {
        var execution = Substitute.For<IExecutionJobReconciler>();
        var sut = new OperationReconcileDispatcher(
            Substitute.For<IWorkflowOperationReconciler>(),
            execution,
            Substitute.For<IMetadataReleaseOperationReconciler>(),
            Substitute.For<ICoordinatedReleaseReconciler>());

        await sut.ReconcileOnceAsync(new OperationRef(OperationKind.ExecutionJob, "   "));

        await execution.DidNotReceiveWithAnyArgs().ReconcileExecutionJobAsync(default!, default);
    }
}
