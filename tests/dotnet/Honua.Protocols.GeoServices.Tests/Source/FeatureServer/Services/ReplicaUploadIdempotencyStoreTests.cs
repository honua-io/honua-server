// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// Unit tests for <see cref="DistributedReplicaUploadIdempotencyStore"/> (#4026): the reservation that
/// holds off an in-flight duplicate upload, the recorded outcome a retry replays, the ownership guard on
/// recording, expiry, and scope isolation. Exercises the in-process path of the shared
/// <see cref="IdempotencyPayloadStore"/>.
/// </summary>
public sealed class ReplicaUploadIdempotencyStoreTests
{
    private static readonly ReplicaUploadRecord Recorded = new() { Fingerprint = "f00d", AppliedAdds = 1, ServerGeneration = 7 };

    private static DistributedReplicaUploadIdempotencyStore CreateStore(TimeProvider? time = null, TimeSpan? reservationWindow = null)
        => new(multiplexer: null, cache: null, NullLogger<DistributedReplicaUploadIdempotencyStore>.Instance, reservationWindow, time);

    [Fact]
    public async Task TryReserveAsync_ConcurrentSameUpload_ExactlyOneWins()
    {
        var store = CreateStore();
        var scope = new ReplicaUploadIdempotencyScope("svc", "replica-1", "alice", "fingerprint:abc:0");

        var tokens = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => store.TryReserveAsync(scope)));

        tokens.Count(token => token is not null).Should().Be(1, "only one in-flight copy of an upload may apply");
        (await store.TryGetAsync(scope)).Should().BeNull("a pending reservation is not a recorded upload");
    }

    [Fact]
    public async Task RecordAsync_WithOwnReservation_RoundTripsTheRecordedOutcome()
    {
        var store = CreateStore();
        var scope = new ReplicaUploadIdempotencyScope("svc", "replica-1", "alice", "key:upload-7");
        var token = await store.TryReserveAsync(scope);
        token.Should().NotBeNull();

        (await store.RecordAsync(scope, token!, new ReplicaUploadRecord
        {
            Fingerprint = "f00d",
            Failed = true,
            AppliedAdds = 2,
            AppliedUpdates = 1,
            AddResults =
            [
                new ServiceLayerEditResult
                {
                    Id = 3,
                    AddResults = [new EditResult { ObjectId = 41, Success = true }, new EditResult { ObjectId = 42, Success = true }]
                }
            ],
            ServerGeneration = 1234
        })).Should().BeTrue();

        var replay = await store.TryGetAsync(scope);
        replay.Should().NotBeNull();
        replay!.Fingerprint.Should().Be("f00d");
        replay.Failed.Should().BeTrue();
        replay.AppliedAdds.Should().Be(2);
        replay.AppliedUpdates.Should().Be(1);
        replay.ServerGeneration.Should().Be(1234);
        replay.AddResults.Should().ContainSingle().Which.Id.Should().Be(3);
        replay.AddResults![0].AddResults!.Select(result => result.ObjectId).Should().Equal(41L, 42L);
        (await store.TryReserveAsync(scope)).Should().BeNull("a recorded upload keeps its key for the dedupe window");
    }

    [Fact]
    public async Task TryGetAsync_DifferentReplicaPrincipalOrKeySpace_DoesNotSeeTheRecord()
    {
        var store = CreateStore();
        var scope = new ReplicaUploadIdempotencyScope("svc", "replica-1", "alice", "key:shared");
        (await store.RecordAsync(scope, "unreserved", Recorded)).Should().BeTrue("an empty key can be recorded");

        (await store.TryGetAsync(scope with { ReplicaId = "replica-2" })).Should().BeNull();
        (await store.TryGetAsync(scope with { Principal = "bob" })).Should().BeNull();
        (await store.TryGetAsync(scope with { UploadKey = "fingerprint:shared:0" })).Should().BeNull();
    }

    [Fact]
    public async Task RecordAsync_WhileAnotherRequestHoldsTheKey_WritesNothing()
    {
        var store = CreateStore();
        var scope = new ReplicaUploadIdempotencyScope("svc", "replica-1", "alice", "fingerprint:owned:3");
        var owner = await store.TryReserveAsync(scope);
        owner.Should().NotBeNull();

        (await store.RecordAsync(scope, "not-the-owner", Recorded)).Should().BeFalse();
        (await store.TryGetAsync(scope)).Should().BeNull("the owner's reservation must survive a foreign record");
        (await store.TryReserveAsync(scope)).Should().BeNull("the owner still holds the key");

        (await store.RecordAsync(scope, owner!, Recorded)).Should().BeTrue();
        (await store.TryGetAsync(scope)).Should().NotBeNull();
    }

    [Fact]
    public async Task RecordAsync_AfterOwnReservationExpiredAndWasRetaken_DoesNotOverwriteTheNewOwner()
    {
        // A request that outlived its reservation must not clobber the retry that took the key over.
        var time = new ManualTimeProvider();
        var store = CreateStore(time);
        var scope = new ReplicaUploadIdempotencyScope("svc", "replica-1", "alice", "fingerprint:slow:3");
        var slow = await store.TryReserveAsync(scope);
        slow.Should().NotBeNull();

        time.Advance(IdempotencyPayloadStore.ReservationWindow + TimeSpan.FromSeconds(1));
        var retry = await store.TryReserveAsync(scope);
        retry.Should().NotBeNull("an expired reservation no longer blocks the key");

        (await store.RecordAsync(scope, slow!, Recorded)).Should().BeFalse();
        (await store.TryGetAsync(scope)).Should().BeNull("the retry's reservation is still pending");
        (await store.RecordAsync(scope, retry!, Recorded)).Should().BeTrue();
    }

    [Fact]
    public async Task TryReserveAsync_AfterReservationExpires_ReservesAgain()
    {
        var time = new ManualTimeProvider();
        var store = CreateStore(time);
        var scope = new ReplicaUploadIdempotencyScope("svc", "replica-1", "alice", "fingerprint:orphan:0");
        (await store.TryReserveAsync(scope)).Should().NotBeNull();

        time.Advance(IdempotencyPayloadStore.ReservationWindow - TimeSpan.FromSeconds(1));
        (await store.TryReserveAsync(scope)).Should().BeNull("the reservation is still live");

        time.Advance(TimeSpan.FromSeconds(2));
        (await store.TryReserveAsync(scope)).Should().NotBeNull("an orphaned reservation must not block its key forever");
    }

    [Fact]
    public async Task TryReserveAsync_AfterDedupeWindow_ForgetsTheRecord()
    {
        var time = new ManualTimeProvider();
        var store = CreateStore(time);
        var scope = new ReplicaUploadIdempotencyScope("svc", "replica-1", "alice", "key:day-old");
        (await store.RecordAsync(scope, (await store.TryReserveAsync(scope))!, Recorded)).Should().BeTrue();

        time.Advance(IdempotencyPayloadStore.DedupeWindow + TimeSpan.FromSeconds(1));

        (await store.TryGetAsync(scope)).Should().BeNull();
        (await store.TryReserveAsync(scope)).Should().NotBeNull("an expired record must not block its key forever");
    }

    [Fact]
    public async Task TryReserveAsync_WindowSizedToRequestTimeout_HoldsForTheWholeRequest()
    {
        DistributedReplicaUploadIdempotencyStore.ReservationWindowFor(TimeSpan.FromSeconds(10))
            .Should().Be(IdempotencyPayloadStore.ReservationWindow, "the window is never shorter than the default");
        var window = DistributedReplicaUploadIdempotencyStore.ReservationWindowFor(TimeSpan.FromMinutes(10));
        window.Should().BeGreaterThan(TimeSpan.FromMinutes(10));

        var time = new ManualTimeProvider();
        var store = CreateStore(time, window);
        var scope = new ReplicaUploadIdempotencyScope("svc", "replica-1", "alice", "fingerprint:large:0");
        (await store.TryReserveAsync(scope)).Should().NotBeNull();

        time.Advance(TimeSpan.FromMinutes(10));
        (await store.TryReserveAsync(scope)).Should().BeNull("a retry must not overtake an upload still inside its request timeout");

        time.Advance(window - TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        (await store.TryReserveAsync(scope)).Should().NotBeNull();
    }

    [Fact]
    public async Task ReleaseAsync_OwnedReservation_FreesTheKeyButNeverARecord()
    {
        var store = CreateStore();
        var scope = new ReplicaUploadIdempotencyScope("svc", "replica-1", "alice", "fingerprint:release:0");
        var token = await store.TryReserveAsync(scope);
        token.Should().NotBeNull();

        await store.ReleaseAsync(scope, token!);
        var retryToken = await store.TryReserveAsync(scope);
        retryToken.Should().NotBeNull("an upload that committed nothing must not block its retry");

        (await store.RecordAsync(scope, retryToken!, Recorded)).Should().BeTrue();
        await store.ReleaseAsync(scope, retryToken!);
        (await store.TryGetAsync(scope)).Should().NotBeNull("a late release must not discard the recorded upload");
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 13, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
