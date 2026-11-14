// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Coordination;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Honua.Infrastructure.SharedCaching;
using Honua.ServiceDefaults;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class CatalogActivationWatcherTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 1, 0, 0, TimeSpan.Zero);
    private readonly ICatalogActivationWatchStore _store = Substitute.For<ICatalogActivationWatchStore>();
    private readonly ICdnInvalidator _cdn = Substitute.For<ICdnInvalidator>();
    private readonly IOpsHealthRollupStore _health = Substitute.For<IOpsHealthRollupStore>();

    [UnitTest]
    public async Task RunOnceAsync_RestartWithPendingPurge_AcknowledgesOnlyAfterSuccess()
    {
        _store.ReadPendingAsync(default).ReturnsForAnyArgs(new[] { Watch() with { RollbackRevision = 3, ChangeId = "catalog-watch-1" } });
        _cdn.InvalidateAsync(default!, default).ReturnsForAnyArgs(new CdnInvalidationResult(CdnInvalidationOutcome.Failed));
        using var watcher = Watcher();
        await watcher.RunOnceAsync(default);
        await _store.DidNotReceiveWithAnyArgs().CompletePurgeAsync(default, default);
        _cdn.InvalidateAsync(default!, default).ReturnsForAnyArgs(new CdnInvalidationResult(CdnInvalidationOutcome.Submitted));
        using var restarted = Watcher();
        await restarted.RunOnceAsync(default);
        await _store.Received(1).CompletePurgeAsync(1, Arg.Any<CancellationToken>());
        await _store.DidNotReceiveWithAnyArgs().TryRollbackAsync(default!, default);
    }

    [UnitTest]
    public async Task RunOnceAsync_ExpiredActivation_ExpiresWithoutReadingTrafficOrPurging()
    {
        _store.ReadPendingAsync(default).ReturnsForAnyArgs(new[] { Watch() with { ActivatedAt = Now.AddHours(-1) } });
        using var watcher = Watcher();
        await watcher.RunOnceAsync(default);
        await _store.Received(1).ExpireAsync(1, Arg.Any<CancellationToken>());
        await _health.DidNotReceiveWithAnyArgs().ReadLatencyAsync(default, default, default, default);
        await _cdn.DidNotReceiveWithAnyArgs().InvalidateAsync(default!, default);
    }

    [UnitTest]
    public async Task RunOnceAsync_HealthyTraffic_DoesNotRollBack()
    {
        _store.ReadPendingAsync(default).ReturnsForAnyArgs(new[] { Watch() });
        _health.ReadLatencyAsync(default, default, default, default).ReturnsForAnyArgs(Array.Empty<OpsHealthLatencyRow>());
        using var watcher = Watcher();
        await watcher.RunOnceAsync(default);
        await _store.DidNotReceiveWithAnyArgs().TryRollbackAsync(default!, default);
        await _cdn.DidNotReceiveWithAnyArgs().InvalidateAsync(default!, default);
    }

    [UnitTest]
    public async Task RunOnceAsync_RegressionLosesConcurrentWriteFence_DoesNotPurge()
    {
        _store.ReadPendingAsync(default).ReturnsForAnyArgs(new[] { Watch() });
        _health.ReadLatencyAsync(default, default, default, default).ReturnsForAnyArgs(new[] { Row(-2, 0), Row(6, 40), Row(7, 40) });
        _store.TryRollbackAsync(default!, default).ReturnsForAnyArgs(false);
        using var watcher = Watcher();
        await watcher.RunOnceAsync(default);
        await _store.Received(1).TryRollbackAsync(Arg.Is<CatalogRollbackRequest>(request => request.ActivationId == 1), Arg.Any<CancellationToken>());
        await _cdn.DidNotReceiveWithAnyArgs().InvalidateAsync(default!, default);
    }

    [UnitTest]
    public async Task RunOnceAsync_RegressionRollsBack_PurgesAndAcknowledgesImmediately()
    {
        _store.ReadPendingAsync(default).ReturnsForAnyArgs(
            new[] { Watch() }, new[] { Watch() with { RollbackRevision = 3, ChangeId = "catalog-watch-1" } });
        _health.ReadLatencyAsync(default, default, default, default)
            .ReturnsForAnyArgs(new[] { Row(-2, 0), Row(6, 40), Row(7, 40) });
        _store.TryRollbackAsync(default!, default).ReturnsForAnyArgs(true);
        _cdn.InvalidateAsync(default!, default).ReturnsForAnyArgs(new CdnInvalidationResult(CdnInvalidationOutcome.Submitted));
        using var watcher = Watcher();
        await watcher.RunOnceAsync(default);
        await _store.Received(1).CompletePurgeAsync(1, Arg.Any<CancellationToken>());
        await _cdn.Received(1).InvalidateAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());
    }

    [UnitTheory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task RunOnceAsync_NonpositiveTelemetryWindow_IgnoresSnapshotsContainingPreactivationTraffic(int seconds)
    {
        var watch = Watch() with { ActivatedAt = Now.AddMinutes(-3) };
        _store.ReadPendingAsync(default).ReturnsForAnyArgs(new[] { watch });
        _health.ReadLatencyAsync(default, default, default, default)
            .ReturnsForAnyArgs(new[]
            {
                Row(-2, 0) with { BucketStart = watch.ActivatedAt.AddMinutes(-2) },
                Row(1, 40) with { BucketStart = watch.ActivatedAt.AddMinutes(1) },
                Row(2, 40) with { BucketStart = watch.ActivatedAt.AddMinutes(2) },
            });

        using var watcher = Watcher(seconds);
        await watcher.RunOnceAsync(default);

        await _store.DidNotReceiveWithAnyArgs().TryRollbackAsync(default!, default);
    }

    [UnitTest]
    public async Task RunOnceAsync_PurgeKeepsFailing_BacksOffInsteadOfRetryingEveryTick()
    {
        _store.ReadPendingAsync(default).ReturnsForAnyArgs(new[] { Watch() with { RollbackRevision = 3, ChangeId = "catalog-watch-1" } });
        _cdn.InvalidateAsync(default!, default).ReturnsForAnyArgs(new CdnInvalidationResult(CdnInvalidationOutcome.Failed));
        using var watcher = Watcher();
        await watcher.RunOnceAsync(default);
        await watcher.RunOnceAsync(default);
        await watcher.RunOnceAsync(default);
        await _cdn.Received(1).InvalidateAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());
        await _store.DidNotReceiveWithAnyArgs().CompletePurgeAsync(default, default);
    }

    [UnitTest]
    public async Task RunOnceAsync_PurgeClaimedByAnotherTask_DoesNotPurgeOrAcknowledge()
    {
        _store.ReadPendingAsync(default).ReturnsForAnyArgs(new[] { Watch() with { RollbackRevision = 3, ChangeId = "catalog-watch-1" } });
        var leases = Substitute.For<ILeaseStore>();
        leases.TryAcquireAsync(default, default, default).ReturnsForAnyArgs(false);
        using var watcher = Watcher(leases: leases);
        await watcher.RunOnceAsync(default);
        await leases.Received(1).TryAcquireAsync(
            Arg.Is<LeaseClaim>(claim => claim.LeaseKey == "catalog-rollback-purge:1"), TimeSpan.FromMinutes(1), Arg.Any<CancellationToken>());
        await _cdn.DidNotReceiveWithAnyArgs().InvalidateAsync(default!, default);
        await _store.DidNotReceiveWithAnyArgs().CompletePurgeAsync(default, default);
    }

    [UnitTest]
    public async Task RunOnceAsync_PurgeSucceedsUnderLease_AcknowledgesAndReleases()
    {
        _store.ReadPendingAsync(default).ReturnsForAnyArgs(new[] { Watch() with { RollbackRevision = 3, ChangeId = "catalog-watch-1" } });
        _cdn.InvalidateAsync(default!, default).ReturnsForAnyArgs(new CdnInvalidationResult(CdnInvalidationOutcome.Submitted));
        var leases = Substitute.For<ILeaseStore>();
        leases.TryAcquireAsync(default, default, default).ReturnsForAnyArgs(true);
        using var watcher = Watcher(leases: leases);
        await watcher.RunOnceAsync(default);
        await _store.Received(1).CompletePurgeAsync(1, Arg.Any<CancellationToken>());
        await leases.Received(1).ReleaseAsync(Arg.Is<LeaseClaim>(claim => claim.LeaseKey == "catalog-rollback-purge:1"), Arg.Any<CancellationToken>());
    }

    [UnitTheory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(4, 16)]
    [InlineData(5, 30)]
    [InlineData(40, 30)]
    public void PurgeBackoff_DoublesFromOneMinuteUpToThirty(int failures, int minutes)
        => Assert.Equal(TimeSpan.FromMinutes(minutes), CatalogActivationWatcher.PurgeBackoff(failures));

    private CatalogActivationWatcher Watcher(int seconds = 300, ILeaseStore? leases = null)
    {
        var collection = new ServiceCollection().AddSingleton(_store).AddSingleton(_health)
            .AddSingleton<TimeProvider>(new FixedTimeProvider())
            .AddSingleton(Options.Create(new TracingOptions { ServingLatencyWindowSeconds = seconds }));
        if (leases is not null)
        {
            collection.AddSingleton(leases);
        }

        var services = collection.BuildServiceProvider();
        return new(services, new CdnInvalidationService(_cdn, NullLogger<CdnInvalidationService>.Instance),
            NullLogger<CatalogActivationWatcher>.Instance);
    }

    private static CatalogActivationWatch Watch() => new()
    {
        Id = 1,
        Revision = 2,
        ExpectedEtag = "etag",
        ActivatedAt = Now.AddMinutes(-8),
    };

    private static OpsHealthLatencyRow Row(int minute, long errors) => new()
    {
        ReplicaId = "host-a",
        BucketStart = Watch().ActivatedAt.AddMinutes(minute),
        Point = new() { Protocol = "OGC", RequestCount = 100, ErrorCount = errors, P50Ms = 1, P95Ms = 1, P99Ms = 1, MaxMs = 1 },
    };

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
