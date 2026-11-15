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
    private readonly FixedTimeProvider _clock = new();

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
        _store.TryRollbackAsync(default!, default).ReturnsForAnyArgs(CatalogRollbackOutcome.NotApplicable);
        using var watcher = Watcher();
        await watcher.RunOnceAsync(default);
        await _store.Received(1).TryRollbackAsync(Arg.Is<CatalogRollbackRequest>(request => request.ActivationId == 1), Arg.Any<CancellationToken>());
        await _cdn.DidNotReceiveWithAnyArgs().InvalidateAsync(default!, default);
    }

    [UnitTest]
    public async Task RunOnceAsync_RegressionOnActivationThatChangedAccess_IsNotRolledBackOrPurged()
    {
        _store.ReadPendingAsync(default).ReturnsForAnyArgs(new[] { Watch() });
        _health.ReadLatencyAsync(default, default, default, default).ReturnsForAnyArgs(new[] { Row(-2, 0), Row(6, 40), Row(7, 40) });
        _store.TryRollbackAsync(default!, default).ReturnsForAnyArgs(CatalogRollbackOutcome.RefusedAccessChange);
        using var watcher = Watcher();
        await watcher.RunOnceAsync(default);
        await _store.Received(1).TryRollbackAsync(Arg.Any<CatalogRollbackRequest>(), Arg.Any<CancellationToken>());
        await _cdn.DidNotReceiveWithAnyArgs().InvalidateAsync(default!, default);
        await _store.DidNotReceiveWithAnyArgs().CompletePurgeAsync(default, default);
    }

    [UnitTest]
    public async Task RunOnceAsync_RegressionRollsBack_PurgesAndAcknowledgesImmediately()
    {
        _store.ReadPendingAsync(default).ReturnsForAnyArgs(
            new[] { Watch() }, new[] { Watch() with { RollbackRevision = 3, ChangeId = "catalog-watch-1" } });
        _health.ReadLatencyAsync(default, default, default, default)
            .ReturnsForAnyArgs(new[] { Row(-2, 0), Row(6, 40), Row(7, 40) });
        _store.TryRollbackAsync(default!, default).ReturnsForAnyArgs(CatalogRollbackOutcome.RolledBack);
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
    public async Task RunOnceAsync_TwoTasksWithFailingCdn_AttemptOncePerWindowAcrossBoth()
    {
        _store.ReadPendingAsync(default).ReturnsForAnyArgs(new[] { Watch() with { RollbackRevision = 3, ChangeId = "catalog-watch-1" } });
        _cdn.InvalidateAsync(default!, default).ReturnsForAnyArgs(new CdnInvalidationResult(CdnInvalidationOutcome.Failed));
        var leases = new ClockLeaseStore(_clock);
        using var first = Watcher(leases: leases);
        using var second = Watcher(leases: leases);

        // The rollback is 8 minutes old: a 2-minute window, then 2.5 minutes once it is 10 minutes old.
        for (var tick = 0; tick <= 8; tick++)
        {
            await first.RunOnceAsync(default);
            await second.RunOnceAsync(default);
            _clock.Advance(TimeSpan.FromSeconds(30));
        }

        await _cdn.Received(2).InvalidateAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());
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
            Arg.Is<LeaseClaim>(claim => claim.LeaseKey == "catalog-rollback-purge:1"), TimeSpan.FromMinutes(2), Arg.Any<CancellationToken>());
        await _cdn.DidNotReceiveWithAnyArgs().InvalidateAsync(default!, default);
        await _store.DidNotReceiveWithAnyArgs().CompletePurgeAsync(default, default);
    }

    [UnitTest]
    public async Task RunOnceAsync_PurgeCompletedElsewhereBeforeLease_DoesNotPurgeAgain()
    {
        _store.ReadPendingAsync(default).ReturnsForAnyArgs(
            new[] { Watch() with { RollbackRevision = 3, ChangeId = "catalog-watch-1" } }, Array.Empty<CatalogActivationWatch>());
        using var watcher = Watcher(leases: new ClockLeaseStore(_clock));
        await watcher.RunOnceAsync(default);
        await _cdn.DidNotReceiveWithAnyArgs().InvalidateAsync(default!, default);
        await _store.DidNotReceiveWithAnyArgs().CompletePurgeAsync(default, default);
    }

    [UnitTest]
    public async Task RunOnceAsync_PurgeSucceedsUnderLease_AcknowledgesAndKeepsLeaseUntilItExpires()
    {
        _store.ReadPendingAsync(default).ReturnsForAnyArgs(new[] { Watch() with { RollbackRevision = 3, ChangeId = "catalog-watch-1" } });
        _cdn.InvalidateAsync(default!, default).ReturnsForAnyArgs(new CdnInvalidationResult(CdnInvalidationOutcome.Submitted));
        var leases = Substitute.For<ILeaseStore>();
        leases.TryAcquireAsync(default, default, default).ReturnsForAnyArgs(true);
        using var watcher = Watcher(leases: leases);
        await watcher.RunOnceAsync(default);
        await _store.Received(1).CompletePurgeAsync(1, Arg.Any<CancellationToken>());
        await leases.DidNotReceiveWithAnyArgs().ReleaseAsync(default, default);
    }

    [UnitTheory]
    [InlineData(0, 60)]
    [InlineData(4, 60)]
    [InlineData(8, 120)]
    [InlineData(10, 150)]
    [InlineData(60, 900)]
    [InlineData(600, 1800)]
    public void PurgeBackoff_IsAQuarterOfTheRollbackAgeBetweenOneAndThirtyMinutes(int ageMinutes, int seconds)
        => Assert.Equal(TimeSpan.FromSeconds(seconds), CatalogActivationWatcher.PurgeBackoff(TimeSpan.FromMinutes(ageMinutes)));

    private CatalogActivationWatcher Watcher(int seconds = 300, ILeaseStore? leases = null)
    {
        var collection = new ServiceCollection().AddSingleton(_store).AddSingleton(_health)
            .AddSingleton<TimeProvider>(_clock)
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
        private DateTimeOffset _now = Now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    // Expiry follows the shared test clock, as the PostgreSQL lease store follows the database clock.
    private sealed class ClockLeaseStore(TimeProvider clock) : ILeaseStore
    {
        private readonly Dictionary<string, (string Owner, DateTimeOffset Expires)> _leases = [];

        public Task<bool> TryAcquireAsync(LeaseClaim claim, TimeSpan duration, CancellationToken cancellationToken = default)
        {
            var now = clock.GetUtcNow();
            if (_leases.TryGetValue(claim.LeaseKey, out var held) && held.Expires > now)
            {
                return Task.FromResult(false);
            }

            _leases[claim.LeaseKey] = (claim.OwnerId, now + duration);
            return Task.FromResult(true);
        }

        public Task<bool> RenewAsync(LeaseClaim claim, TimeSpan duration, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task ReleaseAsync(LeaseClaim claim, CancellationToken cancellationToken = default)
        {
            _leases.Remove(claim.LeaseKey);
            return Task.CompletedTask;
        }
    }
}
