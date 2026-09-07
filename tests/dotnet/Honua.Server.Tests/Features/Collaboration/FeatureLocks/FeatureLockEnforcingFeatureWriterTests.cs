// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Data;
using FluentAssertions;
using Honua.Core.Features.Collaboration.FeatureLocks;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Collaboration;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Tests.Features.Collaboration.FeatureLocks;

/// <summary>
/// Proves the shared edit-pipeline backstop: a write that reaches
/// <see cref="IFeatureWriter"/> without any protocol handler having consulted the lease
/// store is still refused, and the provider writer is never called (#4402).
/// </summary>
/// <remarks>
/// This is the guard for the surfaces that do not have their own handler check — the OData
/// atomic change-set path, WFS-T, and the gRPC feature service all call
/// <c>ApplyEditsAsync</c> directly — and for any write path added later. The inner writer
/// here records whether it was invoked, so "nothing was written" is asserted rather than
/// inferred.
/// </remarks>
[Protocol(TestProtocols.Infrastructure)]
public sealed class FeatureLockEnforcingFeatureWriterTests
{
    private const int StorageLayerId = 4;
    private const string ServiceName = "parcels";

    [UnitTest]
    [Operation(Operations.Update)]
    public async Task ApplyEditsAsync_UpdateOfALeasedFeature_RollsBackWithoutCallingTheProvider()
    {
        var (writer, locks, inner) = CreateWriter();
        await ClaimAsync(locks, objectId: 42);

        var result = await writer.ApplyEditsAsync(
            StorageLayerId,
            new FeatureEditBatch { Updates = [FeatureWithId(42)] });

        result.WasRolledBack.Should().BeTrue();
        result.UpdatedCount.Should().Be(0);
        result.UpdateResults.Should().ContainSingle()
            .Which.ErrorCode.Should().Be(FeatureLockEnforcingFeatureWriter.LockedErrorCode);
        result.UpdateResults[0].ErrorMessage.Should().Contain(
            "Alice Editor",
            "the refusal must name the blocking editor so a client can prompt for it");
        inner.ApplyEditsCalls.Should().Be(0, "a blocked batch must never reach the provider");
    }

    [UnitTest]
    [Operation(Operations.Delete)]
    public async Task ApplyEditsAsync_DeleteOfALeasedFeature_RollsBackWithoutCallingTheProvider()
    {
        var (writer, locks, inner) = CreateWriter();
        await ClaimAsync(locks, objectId: 42);

        var result = await writer.ApplyEditsAsync(
            StorageLayerId,
            new FeatureEditBatch { Deletes = [42L] });

        result.WasRolledBack.Should().BeTrue();
        result.DeleteResults.Should().ContainSingle()
            .Which.ErrorCode.Should().Be(FeatureLockEnforcingFeatureWriter.LockedErrorCode);
        inner.ApplyEditsCalls.Should().Be(0);
    }

    [UnitTest]
    [Operation(Operations.Update)]
    public async Task ApplyEditsAsync_OrderedOperationsTouchingALeasedFeature_RollsBackTheWholeBatch()
    {
        // The ordered-Operations shape is what the OGC batch and the OData change-set build.
        // One leased target must stop the batch, not just its own row: these callers run with
        // rollback semantics, so a partially-applied batch would be worse than none.
        var (writer, locks, inner) = CreateWriter();
        await ClaimAsync(locks, objectId: 42);

        var result = await writer.ApplyEditsAsync(
            StorageLayerId,
            new FeatureEditBatch
            {
                Operations =
                [
                    FeatureEditOperation.Create(FeatureWithId(0)),
                    FeatureEditOperation.Update(FeatureWithId(42))
                ]
            });

        result.WasRolledBack.Should().BeTrue();
        result.CreateResults.Should().ContainSingle().Which.IsSuccess.Should().BeFalse();
        result.UpdateResults.Should().ContainSingle()
            .Which.ErrorCode.Should().Be(FeatureLockEnforcingFeatureWriter.LockedErrorCode);
        inner.ApplyEditsCalls.Should().Be(0);
    }

    [UnitTest]
    [Operation(Operations.Create)]
    public async Task ApplyEditsAsync_CreateWhileAnotherFeatureIsLeased_PassesThrough()
    {
        // A feature that does not exist yet cannot be leased, so inserts are never blocked.
        var (writer, locks, inner) = CreateWriter();
        await ClaimAsync(locks, objectId: 42);

        var result = await writer.ApplyEditsAsync(
            StorageLayerId,
            new FeatureEditBatch { Creates = [FeatureWithId(0)] });

        result.WasRolledBack.Should().BeFalse();
        inner.ApplyEditsCalls.Should().Be(1);
    }

    [UnitTest]
    [Operation(Operations.Update)]
    public async Task ApplyEditsAsync_UnleasedFeature_PassesThrough()
    {
        var (writer, locks, inner) = CreateWriter();
        await ClaimAsync(locks, objectId: 42);

        var result = await writer.ApplyEditsAsync(
            StorageLayerId,
            new FeatureEditBatch { Updates = [FeatureWithId(43)] });

        result.WasRolledBack.Should().BeFalse();
        inner.ApplyEditsCalls.Should().Be(1);
    }

    [UnitTest]
    [Operation(Operations.Update)]
    public async Task ApplyEditsAsync_WithNoLeaseAnywhere_PassesThroughWithoutTouchingTheGraph()
    {
        // The uncontended path: one emptiness probe, no metadata resolution, no per-feature
        // lookups. This is what keeps always-on enforcement free for deployments that never
        // hand out a lease — which is every deployment until an authorizer is supplied.
        var (writer, _, inner) = CreateWriter(out var metadata);

        var result = await writer.ApplyEditsAsync(
            StorageLayerId,
            new FeatureEditBatch { Updates = [FeatureWithId(42)] });

        result.WasRolledBack.Should().BeFalse();
        inner.ApplyEditsCalls.Should().Be(1);
        metadata.Reads.Should().Be(0, "an empty lease store must not cost a graph read");
    }

    [UnitTest]
    [Operation(Operations.Update)]
    public async Task ApplyEditsAsync_ByTheLeaseHolder_PassesThrough()
    {
        var (writer, locks, inner) = CreateWriter(holderId: "alice");
        await ClaimAsync(locks, objectId: 42);

        var result = await writer.ApplyEditsAsync(
            StorageLayerId,
            new FeatureEditBatch { Updates = [FeatureWithId(42)] });

        result.WasRolledBack.Should().BeFalse();
        inner.ApplyEditsCalls.Should().Be(1);
    }

    [UnitTest]
    [Operation(Operations.Delete)]
    public async Task DeleteAsync_OfALeasedFeature_ThrowsAndLeavesTheProviderUntouched()
    {
        var (writer, locks, inner) = CreateWriter();
        await ClaimAsync(locks, objectId: 42);

        var act = () => writer.DeleteAsync(StorageLayerId, 42);

        await act.Should().ThrowAsync<FeatureLockedException>();
        inner.DeleteCalls.Should().Be(0);
    }

    private static Feature FeatureWithId(long id)
        => new()
        {
            Id = id,
            Geometry = null,
            Attributes = ImmutableDictionary<string, object?>.Empty
        };

    private static ValueTask<FeatureLockClaimResponse> ClaimAsync(IFeatureLockService locks, long objectId)
        => locks.ClaimAsync(
            FeatureRef.Canonical(ServiceName, StorageLayerId, objectId),
            new LockHolder("alice", "Alice Editor"),
            TimeSpan.FromMinutes(5),
            FeatureLockAccessContext.AuthorizedWrite);

    private static (FeatureLockEnforcingFeatureWriter Writer, IFeatureLockService Locks, RecordingFeatureWriter Inner)
        CreateWriter(string? holderId = null)
        => CreateWriter(out _, holderId);

    private static (FeatureLockEnforcingFeatureWriter Writer, IFeatureLockService Locks, RecordingFeatureWriter Inner)
        CreateWriter(out CountingGraphProvider metadata, string? holderId = null)
    {
        var inner = new RecordingFeatureWriter();
        var locks = new InMemoryFeatureLockService();
        var guard = new FeatureEditGuard(locks);
        metadata = new CountingGraphProvider(BuildSnapshot());

        var httpContext = new DefaultHttpContext();
        if (holderId is not null)
        {
            httpContext.Request.Headers[FeatureEditLockEnforcement.HolderHeaderName] = holderId;
        }

        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        return (new FeatureLockEnforcingFeatureWriter(inner, locks, guard, metadata, accessor), locks, inner);
    }

    /// <summary>
    /// One service named <c>parcels</c> publishing the storage layer under test, so the
    /// writer can map its integer layer handle back to the client-facing lease namespace.
    /// </summary>
    private static MetadataV2GraphSnapshot BuildSnapshot()
    {
        var graph = new MetadataV2Graph
        {
            Services =
            [
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "svc-parcels", Name = ServiceName }
                }
            ],
            Publications =
            [
                new MetadataV2Publication
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "pub-parcels", Name = "0" },
                    ServiceId = "svc-parcels",
                    ResourceId = "res-parcels",
                    Identifier = new MetadataV2PublicationIdentifier
                    {
                        Value = StorageLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        IsNumeric = true
                    }
                }
            ]
        };

        return new MetadataV2GraphSnapshot(graph, "etag", DateTimeOffset.UnixEpoch);
    }

    private sealed class CountingGraphProvider(MetadataV2GraphSnapshot snapshot) : IMetadataV2GraphProvider
    {
        public int Reads { get; private set; }

        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
        {
            Reads++;
            return ValueTask.FromResult(snapshot);
        }

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<MetadataV2GraphSnapshot?>(snapshot);
    }

    /// <summary>
    /// Stands in for the provider writer and records whether it was reached. Nothing about
    /// the lease decision depends on it, which is the point: the assertions above prove the
    /// decorator stopped short of the provider rather than that the provider declined.
    /// </summary>
    private sealed class RecordingFeatureWriter : IFeatureWriter
    {
        public int ApplyEditsCalls { get; private set; }

        public int DeleteCalls { get; private set; }

        public Task<IFeatureWriterTransaction> BeginTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<Feature> CreateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
            => Task.FromResult(feature);

        public Task<Feature> UpdateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
            => Task.FromResult(feature);

        public Task<bool> DeleteAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            return Task.FromResult(true);
        }

        public Task<FeatureEditResult> ApplyEditsAsync(
            int layerId,
            FeatureEditBatch editBatch,
            CancellationToken cancellationToken = default)
        {
            ApplyEditsCalls++;
            return Task.FromResult(FeatureEditResult.Success(
                editBatch.Creates.IsDefaultOrEmpty ? 0 : editBatch.Creates.Length,
                editBatch.Updates.IsDefaultOrEmpty ? 0 : editBatch.Updates.Length,
                editBatch.Deletes.IsDefaultOrEmpty ? 0 : editBatch.Deletes.Length,
                createdIds: ImmutableArray<long>.Empty));
        }
    }
}
