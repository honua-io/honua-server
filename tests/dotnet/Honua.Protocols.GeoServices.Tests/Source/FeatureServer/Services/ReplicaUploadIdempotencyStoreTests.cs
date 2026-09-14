// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// Unit tests for <see cref="DistributedReplicaUploadIdempotencyStore"/> (#4026): the reservation that
/// holds off an in-flight duplicate upload, the recorded outcome a retry replays, and scope isolation.
/// </summary>
public sealed class ReplicaUploadIdempotencyStoreTests
{
    private static DistributedReplicaUploadIdempotencyStore CreateStore()
        => new(multiplexer: null, cache: null, NullLogger<DistributedReplicaUploadIdempotencyStore>.Instance);

    [Fact]
    public async Task TryReserveAsync_ConcurrentSameUpload_ExactlyOneWins()
    {
        var store = CreateStore();
        var scope = new ReplicaUploadIdempotencyScope("svc", "replica-1", "alice", "fingerprint:abc");

        var tokens = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => store.TryReserveAsync(scope)));

        tokens.Count(token => token is not null).Should().Be(1, "only one in-flight copy of an upload may apply");
        (await store.TryGetAsync(scope)).Should().BeNull("a pending reservation is not a recorded upload");
    }

    [Fact]
    public async Task SetAsync_AfterReservation_RoundTripsTheRecordedOutcome()
    {
        var store = CreateStore();
        var scope = new ReplicaUploadIdempotencyScope("svc", "replica-1", "alice", "key:upload-7");
        (await store.TryReserveAsync(scope)).Should().NotBeNull();

        await store.SetAsync(scope, new ReplicaUploadRecord
        {
            Fingerprint = "f00d",
            AppliedAdds = 2,
            AppliedUpdates = 1,
            AppliedDeletes = 0,
            AddResults =
            [
                new ServiceLayerEditResult
                {
                    Id = 3,
                    AddResults = [new EditResult { ObjectId = 41, Success = true }, new EditResult { ObjectId = 42, Success = true }]
                }
            ],
            ServerGeneration = 1234
        });

        var replay = await store.TryGetAsync(scope);
        replay.Should().NotBeNull();
        replay!.Fingerprint.Should().Be("f00d");
        replay.AppliedAdds.Should().Be(2);
        replay.AppliedUpdates.Should().Be(1);
        replay.ServerGeneration.Should().Be(1234);
        replay.AddResults.Should().ContainSingle().Which.Id.Should().Be(3);
        replay.AddResults![0].AddResults!.Select(result => result.ObjectId).Should().Equal(41L, 42L);
        (await store.TryReserveAsync(scope)).Should().BeNull("a recorded upload keeps its key for the dedupe window");
    }

    [Fact]
    public async Task TryGetAsync_DifferentReplicaOrPrincipal_DoesNotSeeTheRecord()
    {
        var store = CreateStore();
        var scope = new ReplicaUploadIdempotencyScope("svc", "replica-1", "alice", "key:shared");
        await store.SetAsync(scope, new ReplicaUploadRecord { Fingerprint = "f", ServerGeneration = 9 });

        (await store.TryGetAsync(scope with { ReplicaId = "replica-2" })).Should().BeNull();
        (await store.TryGetAsync(scope with { Principal = "bob" })).Should().BeNull();
        (await store.TryGetAsync(scope with { UploadKey = "fingerprint:shared" })).Should().BeNull();
    }

    [Fact]
    public async Task ReleaseAsync_OwnedReservation_FreesTheKeyButNeverARecord()
    {
        var store = CreateStore();
        var scope = new ReplicaUploadIdempotencyScope("svc", "replica-1", "alice", "fingerprint:release");
        var token = await store.TryReserveAsync(scope);
        token.Should().NotBeNull();

        await store.ReleaseAsync(scope, token!);
        var retryToken = await store.TryReserveAsync(scope);
        retryToken.Should().NotBeNull("an upload that committed nothing must not block its retry");

        await store.SetAsync(scope, new ReplicaUploadRecord { Fingerprint = "f", ServerGeneration = 5 });
        await store.ReleaseAsync(scope, retryToken!);
        (await store.TryGetAsync(scope)).Should().NotBeNull("a late release must not discard the recorded upload");
    }
}
