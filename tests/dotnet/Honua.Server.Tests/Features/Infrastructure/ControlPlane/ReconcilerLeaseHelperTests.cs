// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class ReconcilerLeaseHelperTests
{
    [UnitTest]
    public async Task RenewLeaseUntilCancelledAsync_WhenStoreThrows_CancelsReconciliationAndCompletesCleanly()
    {
        using var cts = new CancellationTokenSource();
        var renewalTask = ReconcilerLeaseHelper.RenewLeaseUntilCancelledAsync(
            "op-1",
            TimeSpan.FromMilliseconds(10),
            cts,
            _ => Task.FromException<bool>(new InvalidOperationException("transient db error")),
            NullLogger.Instance);

        await renewalTask.WaitAsync(TimeSpan.FromSeconds(5));

        cts.IsCancellationRequested.Should().BeTrue();
    }

    [UnitTest]
    public async Task StopAndReleaseLeaseAsync_WhenRenewalTaskFaulted_StillReleasesLeaseWithBoundedToken()
    {
        using var cts = new CancellationTokenSource();
        var faultedRenewal = Task.FromException(new InvalidOperationException("renewal exploded"));
        CancellationToken observedReleaseToken = default;
        var releaseCalls = 0;

        await ReconcilerLeaseHelper.StopAndReleaseLeaseAsync(
            "op-2",
            cts,
            faultedRenewal,
            ct =>
            {
                releaseCalls++;
                observedReleaseToken = ct;
                return Task.CompletedTask;
            },
            NullLogger.Instance);

        releaseCalls.Should().Be(1);
        observedReleaseToken.CanBeCanceled.Should().BeTrue();
        observedReleaseToken.IsCancellationRequested.Should().BeFalse();
    }

    [UnitTest]
    public async Task CoordinatedReleaseReconciler_HostShutdownCancellation_DoesNotMarkManualInterventionAndReleasesLease()
    {
        var store = Substitute.For<IWorkflowOperationStore>();
        var containerStep = Substitute.For<ICoordinatedContainerStepExecutor>();
        var metadataStep = Substitute.For<ICoordinatedMetadataStepExecutor>();

        using var shutdownCts = new CancellationTokenSource();
        CancellationToken? releaseToken = null;

        store.TryAcquireLeaseAsync("op-shutdown", Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(true);
        store.GetAsync("op-shutdown", Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                shutdownCts.Cancel();
                return Task.FromException<WorkflowOperationRecord?>(new OperationCanceledException(shutdownCts.Token));
            });
        store.ReleaseLeaseAsync("op-shutdown", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                releaseToken = call.ArgAt<CancellationToken>(2);
                return Task.CompletedTask;
            });

        var reconciler = new CoordinatedReleaseReconciler(
            store,
            containerStep,
            metadataStep,
            NullLogger<CoordinatedReleaseReconciler>.Instance);

        var act = () => reconciler.ReconcileCoordinatedReleaseAsync("op-shutdown", shutdownCts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        await store.DidNotReceive().SetAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
        await store.DidNotReceive().TrySetAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
        releaseToken.Should().NotBeNull();
        releaseToken!.Value.IsCancellationRequested.Should().BeFalse();
    }
}
