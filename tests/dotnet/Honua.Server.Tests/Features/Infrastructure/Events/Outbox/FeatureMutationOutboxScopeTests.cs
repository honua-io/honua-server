// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Events.Outbox;
using Honua.Server.Features.Infrastructure.Events;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.Events.Outbox;

[Protocol(TestProtocols.TestQuality)]
public sealed class FeatureMutationOutboxScopeTests
{
    [UnitTest]
    public void Begin_FlowsScopeAcrossAwait_AndRestoresOnDispose()
    {
        FeatureMutationOutboxScope.Current.Should().BeNull();

        var scope = new FeatureMutationOutboxScopeData
        {
            EntryFactory = (_, _, _) => null
        };

        using (FeatureMutationOutboxScope.Begin(scope))
        {
            FeatureMutationOutboxScope.Current.Should().BeSameAs(scope);
        }

        FeatureMutationOutboxScope.Current.Should().BeNull();
    }

    [UnitTest]
    public async Task Current_FlowsAcrossAwaits()
    {
        var scope = new FeatureMutationOutboxScopeData
        {
            EntryFactory = (_, _, _) => null
        };

        using var _ = FeatureMutationOutboxScope.Begin(scope);
        await Task.Yield();
        FeatureMutationOutboxScope.Current.Should().BeSameAs(scope);
    }

    [UnitTest]
    public async Task ResolveOutboxScopeAsync_WhenCapabilityFalse_ReturnsNullAndDoesNotSetScope()
    {
        var publisher = Substitute.For<IFeatureChangeEventPublisher>();
        var capability = Substitute.For<IOutboxCapabilityProvider>();
        capability.SupportsTransactionalOutbox.Returns(false);
        var service = new FeatureMutationEventService(publisher, outboxCapabilityProvider: capability);

        var context = new DefaultHttpContext { TraceIdentifier = "trace-no-outbox" };

        var data = await service.ResolveOutboxScopeAsync(context, layerId: 1, protocol: "OgcFeatures");
        using var scope = FeatureMutationOutboxScope.BeginIfNotNull(data);

        data.Should().BeNull();
        FeatureMutationOutboxScope.Current.Should().BeNull("non-capable providers must not arm the outbox scope");
        service.OutboxEnabled.Should().BeFalse();
    }

    [UnitTest]
    public async Task ResolveOutboxScopeAsync_WhenCapabilityTrue_BuildsEntryFactoryFromProtocolContext()
    {
        var publisher = Substitute.For<IFeatureChangeEventPublisher>();
        var capability = Substitute.For<IOutboxCapabilityProvider>();
        capability.SupportsTransactionalOutbox.Returns(true);
        var service = new FeatureMutationEventService(publisher, outboxCapabilityProvider: capability);

        var context = new DefaultHttpContext { TraceIdentifier = "trace-outbox" };

        var data = await service.ResolveOutboxScopeAsync(
            context,
            layerId: 7,
            protocol: "Grpc",
            sourceId: "svc-grpc",
            serviceId: "service-7",
            requestId: "req-7",
            layerSrid: 4326);

        using (FeatureMutationOutboxScope.BeginIfNotNull(data))
        {
            FeatureMutationOutboxScope.Current.Should().NotBeNull();
            var feature = Feature.Create(99, geometry: null, ImmutableDictionary<string, object?>.Empty);
            var entry = FeatureMutationOutboxScope.Current!.EntryFactory(99, "create", feature);
            entry.Should().NotBeNull();
            entry!.ServiceId.Should().Be("service-7");
            entry.LayerId.Should().Be(7);
            entry.ObjectId.Should().Be(99);
            entry.Operation.Should().Be("create");
            entry.Protocol.Should().Be("Grpc");
            entry.SourceId.Should().Be("svc-grpc");
            entry.RequestId.Should().Be("req-7");
            entry.Status.Should().Be(OutboxStatuses.Pending);
            entry.RetryCount.Should().Be(0);
            entry.EventPayload.Should().Contain("\"ObjectId\":99");
            entry.EventPayload.Should().Contain("\"Operation\":\"create\"");
        }

        FeatureMutationOutboxScope.Current.Should().BeNull();
    }

    [UnitTest]
    public async Task ResolveOutboxScopeAsync_OutboxEntry_CarriesMutationTimestampForDelayedDispatch()
    {
        // Outbox payload-time guard (#692): when the mutation is committed at T1 but the
        // dispatcher only appends the canonical event at T2 (e.g., Postgres claim lease
        // expired and recovery re-claimed the row hours later), replay/from filtering
        // should still see T1 — not T2. We capture the mutation timestamp once in
        // BuildEntry and serialize it into the outbox payload so InMemoryFeatureChangeEventStore.AppendAsync
        // honors it instead of falling back to its DateTimeOffset.UtcNow default.
        var publisher = Substitute.For<IFeatureChangeEventPublisher>();
        var capability = Substitute.For<IOutboxCapabilityProvider>();
        capability.SupportsTransactionalOutbox.Returns(true);
        var service = new FeatureMutationEventService(publisher, outboxCapabilityProvider: capability);

        var context = new DefaultHttpContext { TraceIdentifier = "trace-time" };
        var beforeCreate = DateTimeOffset.UtcNow;
        var data = await service.ResolveOutboxScopeAsync(
            context,
            layerId: 7,
            protocol: "OgcFeatures",
            requestId: "req-time");
        using var _ = FeatureMutationOutboxScope.BeginIfNotNull(data);
        var feature = Feature.Create(42, geometry: null, ImmutableDictionary<string, object?>.Empty);
        var entry = FeatureMutationOutboxScope.Current!.EntryFactory(42, "update", feature);
        var afterCreate = DateTimeOffset.UtcNow;

        entry.Should().NotBeNull();
        // CreatedAt and the serialized payload's Timestamp must agree on the same captured
        // mutation time. Otherwise dispatcher append time would re-stamp the event.
        entry!.CreatedAt.Should().BeOnOrAfter(beforeCreate).And.BeOnOrBefore(afterCreate);
        var payload = System.Text.Json.JsonSerializer.Deserialize(
            entry.EventPayload,
            FeatureChangeEventsJsonContext.Default.FeatureChangeEventRequest);
        payload.Should().NotBeNull();
        payload!.Timestamp.Should().NotBeNull("delayed dispatch must replay at the original mutation time, not at dispatcher append time");
        payload.Timestamp!.Value.Should().Be(entry.CreatedAt);
    }

    [UnitTest]
    public async Task ResolveOutboxScopeAsync_ExplicitGeometryChangedTrue_EncodesTrue()
    {
        // GeometryChanged contract (#692): the outbox payload mirrors the protocol-layer
        // publish path's explicit geometryChanged signal — the source of truth for whether
        // the originating request intended to mutate geometry — rather than inferring from
        // the post-mutation snapshot.
        var publisher = Substitute.For<IFeatureChangeEventPublisher>();
        var capability = Substitute.For<IOutboxCapabilityProvider>();
        capability.SupportsTransactionalOutbox.Returns(true);
        var service = new FeatureMutationEventService(publisher, outboxCapabilityProvider: capability);

        var context = new DefaultHttpContext { TraceIdentifier = "trace-geom" };
        var data = await service.ResolveOutboxScopeAsync(
            context,
            layerId: 7,
            protocol: "OgcFeatures",
            requestId: "req-geom",
            layerSrid: 4326,
            geometryChanged: true);

        using var _ = FeatureMutationOutboxScope.BeginIfNotNull(data);
        var withGeometry = Feature.Create(11, geometry: new byte[] { 0x01, 0x02 }, ImmutableDictionary<string, object?>.Empty);
        var entry = FeatureMutationOutboxScope.Current!.EntryFactory(11, "update", withGeometry);

        entry.Should().NotBeNull();
        entry!.EventPayload.Should().Contain("\"GeometryChanged\":true");
    }

    [UnitTest]
    public async Task ResolveOutboxScopeAsync_PatchPreservesGeometry_EncodesGeometryChangedFalse()
    {
        // OData PATCH regression guard (#692): an attribute-only PATCH on a spatial
        // feature returns a snapshot with geometry (the prior WKB), but the protocol-
        // layer geometryChanged signal is false. The outbox payload must follow the
        // protocol signal, not the snapshot heuristic, otherwise PATCH is reported as
        // a geometry change to streaming/webhook subscribers.
        var publisher = Substitute.For<IFeatureChangeEventPublisher>();
        var capability = Substitute.For<IOutboxCapabilityProvider>();
        capability.SupportsTransactionalOutbox.Returns(true);
        var service = new FeatureMutationEventService(publisher, outboxCapabilityProvider: capability);

        var context = new DefaultHttpContext { TraceIdentifier = "trace-patch" };
        var data = await service.ResolveOutboxScopeAsync(
            context,
            layerId: 7,
            protocol: "OData",
            requestId: "req-patch",
            geometryChanged: false);

        using var _ = FeatureMutationOutboxScope.BeginIfNotNull(data);
        // Snapshot still has geometry (PATCH preserves the prior WKB); the explicit
        // geometryChanged: false from the protocol must win over the snapshot heuristic.
        var preservedGeometrySnapshot = Feature.Create(12, geometry: new byte[] { 0x01, 0x02, 0x03 }, ImmutableDictionary<string, object?>.Empty);
        var entry = FeatureMutationOutboxScope.Current!.EntryFactory(12, "update", preservedGeometrySnapshot);

        entry.Should().NotBeNull();
        entry!.EventPayload.Should().Contain("\"GeometryChanged\":false");
    }

    [UnitTest]
    public async Task ResolveOutboxScopeAsync_DefaultGeometryChanged_EncodesFalse()
    {
        // When the protocol caller does not signal geometryChanged, default to false to
        // match the inline publish path's contract (legacy callers that never set the
        // parameter must not silently flip to true). This keeps PATCH/merge updates
        // from being over-reported as geometry changes.
        var publisher = Substitute.For<IFeatureChangeEventPublisher>();
        var capability = Substitute.For<IOutboxCapabilityProvider>();
        capability.SupportsTransactionalOutbox.Returns(true);
        var service = new FeatureMutationEventService(publisher, outboxCapabilityProvider: capability);

        var context = new DefaultHttpContext { TraceIdentifier = "trace-default" };
        var data = await service.ResolveOutboxScopeAsync(
            context,
            layerId: 7,
            protocol: "OData",
            requestId: "req-default");

        using var _ = FeatureMutationOutboxScope.BeginIfNotNull(data);
        var snapshotWithGeometry = Feature.Create(13, geometry: new byte[] { 0x42 }, ImmutableDictionary<string, object?>.Empty);
        var entry = FeatureMutationOutboxScope.Current!.EntryFactory(13, "update", snapshotWithGeometry);

        entry.Should().NotBeNull();
        entry!.EventPayload.Should().Contain("\"GeometryChanged\":false");
    }

    [UnitTest]
    public async Task ResolveOutboxScopeAsync_BeginRowAttempt_AdvancesQueueOnFailedRows()
    {
        // Partial-success regression guard (#692): non-rollback batches catch row mutation
        // failures and continue. Without BeginRowAttempt, the failed row never reaches
        // EntryFactory, so its queued GeometryChanged / requestId stays at the head of the
        // queue and the next successful row of the same kind dequeues it. Calling
        // BeginRowAttempt once per attempted row binds the per-row metadata so a failed row
        // discards its slot and the next row reads its own.
        var publisher = Substitute.For<IFeatureChangeEventPublisher>();
        var capability = Substitute.For<IOutboxCapabilityProvider>();
        capability.SupportsTransactionalOutbox.Returns(true);
        var service = new FeatureMutationEventService(publisher, outboxCapabilityProvider: capability);

        var context = new DefaultHttpContext { TraceIdentifier = "trace-partial" };
        var perOpRequestIds = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["update"] = new[] { "trace-partial:u1", "trace-partial:u2", "trace-partial:u3" },
        };
        var perOpGeometryChanged = new Dictionary<string, IReadOnlyList<bool>>(StringComparer.Ordinal)
        {
            ["update"] = new[] { true, false, true },
        };

        var data = await service.ResolveOutboxScopeAsync(
            context,
            layerId: 7,
            protocol: "Wfs20",
            perOperationRequestIds: perOpRequestIds,
            perOperationGeometryChanged: perOpGeometryChanged);

        using var _ = FeatureMutationOutboxScope.BeginIfNotNull(data);
        var feature = Feature.Create(0, geometry: null, ImmutableDictionary<string, object?>.Empty);

        // Row 0: attempted, succeeded. Should consume slot 0.
        FeatureMutationOutboxScope.Current!.BeginRowAttempt!("update");
        var row0 = FeatureMutationOutboxScope.Current!.EntryFactory(101, "update", feature);

        // Row 1: attempted, FAILED. BeginRowAttempt advances; EntryFactory is NOT called.
        FeatureMutationOutboxScope.Current!.BeginRowAttempt!("update");
        // (no EntryFactory call — the data layer's catch block records the failure result)

        // Row 2: attempted, succeeded. Should consume slot 2 — not slot 1's leftover.
        FeatureMutationOutboxScope.Current!.BeginRowAttempt!("update");
        var row2 = FeatureMutationOutboxScope.Current!.EntryFactory(103, "update", feature);

        row0!.RequestId.Should().Be("trace-partial:u1");
        row0.EventPayload.Should().Contain("\"GeometryChanged\":true");
        row2!.RequestId.Should().Be("trace-partial:u3", "the failed row's slot must not shift onto the next successful row");
        row2.EventPayload.Should().Contain("\"GeometryChanged\":true");
    }

    [UnitTest]
    public async Task ResolveOutboxScopeAsync_BeginRowAttempt_NoOpWithoutQueues()
    {
        // Single-row scopes (CRUD endpoints) do not seed per-operation queues. BeginRowAttempt
        // must still be safe to call, leaving the scope-wide defaults bound for EntryFactory.
        var publisher = Substitute.For<IFeatureChangeEventPublisher>();
        var capability = Substitute.For<IOutboxCapabilityProvider>();
        capability.SupportsTransactionalOutbox.Returns(true);
        var service = new FeatureMutationEventService(publisher, outboxCapabilityProvider: capability);

        var context = new DefaultHttpContext { TraceIdentifier = "trace-no-queues" };
        var data = await service.ResolveOutboxScopeAsync(
            context,
            layerId: 1,
            protocol: "OData",
            requestId: "req-no-queues",
            geometryChanged: true);

        using var _ = FeatureMutationOutboxScope.BeginIfNotNull(data);
        var feature = Feature.Create(0, geometry: null, ImmutableDictionary<string, object?>.Empty);

        FeatureMutationOutboxScope.Current!.BeginRowAttempt!("update");
        var entry = FeatureMutationOutboxScope.Current!.EntryFactory(42, "update", feature);

        entry!.RequestId.Should().Be("req-no-queues");
        entry.EventPayload.Should().Contain("\"GeometryChanged\":true");
    }

    [UnitTest]
    public async Task ResolveOutboxScopeAsync_PerOperationGeometryChanged_DequeuesInOrderAndFallsBack()
    {
        // Atomic batch path (#692): per-row geometryChanged must dequeue from the kind-keyed
        // queue in input order. When the queue runs out, fall back to the scope-wide bool
        // (default false) so the row's GeometryChanged is never under-/over-reported by an
        // out-of-band heuristic.
        var publisher = Substitute.For<IFeatureChangeEventPublisher>();
        var capability = Substitute.For<IOutboxCapabilityProvider>();
        capability.SupportsTransactionalOutbox.Returns(true);
        var service = new FeatureMutationEventService(publisher, outboxCapabilityProvider: capability);

        var context = new DefaultHttpContext { TraceIdentifier = "trace-geom-batch" };
        var perOpGeometryChanged = new Dictionary<string, IReadOnlyList<bool>>(StringComparer.Ordinal)
        {
            ["create"] = new[] { true, false },
            ["update"] = new[] { false },
        };

        var data = await service.ResolveOutboxScopeAsync(
            context,
            layerId: 7,
            protocol: "OData",
            perOperationGeometryChanged: perOpGeometryChanged);

        using var _ = FeatureMutationOutboxScope.BeginIfNotNull(data);
        var feature = Feature.Create(0, geometry: new byte[] { 0x01 }, ImmutableDictionary<string, object?>.Empty);

        // Each row attempt advances the queue via BeginRowAttempt; EntryFactory then
        // reads the currently bound metadata.
        FeatureMutationOutboxScope.Current!.BeginRowAttempt!("create");
        var firstCreate = FeatureMutationOutboxScope.Current!.EntryFactory(101, "create", feature);
        FeatureMutationOutboxScope.Current!.BeginRowAttempt!("create");
        var secondCreate = FeatureMutationOutboxScope.Current!.EntryFactory(102, "create", feature);
        FeatureMutationOutboxScope.Current!.BeginRowAttempt!("update");
        var firstUpdate = FeatureMutationOutboxScope.Current!.EntryFactory(201, "update", feature);
        // Queue exhausted: should fall back to scope-wide default (false).
        FeatureMutationOutboxScope.Current!.BeginRowAttempt!("create");
        var thirdCreate = FeatureMutationOutboxScope.Current!.EntryFactory(103, "create", feature);
        // No queue for delete: falls back to default false too.
        FeatureMutationOutboxScope.Current!.BeginRowAttempt!("delete");
        var firstDelete = FeatureMutationOutboxScope.Current!.EntryFactory(301, "delete", feature);

        firstCreate!.EventPayload.Should().Contain("\"GeometryChanged\":true");
        secondCreate!.EventPayload.Should().Contain("\"GeometryChanged\":false");
        firstUpdate!.EventPayload.Should().Contain("\"GeometryChanged\":false");
        thirdCreate!.EventPayload.Should().Contain("\"GeometryChanged\":false");
        firstDelete!.EventPayload.Should().Contain("\"GeometryChanged\":false");
    }

    [UnitTest]
    public async Task ResolveOutboxScopeAsync_PerOperationRequestIds_DequeuesInOrderAndFallsBack()
    {
        // Atomic batch correlation guard (#692): the entry factory must consume the
        // per-operation queues in input order so each outbox row carries the originating
        // subrequest id; once a queue is exhausted, subsequent rows fall back to the
        // resolved scope-wide id (parent trace identifier when no requestId was provided).
        var publisher = Substitute.For<IFeatureChangeEventPublisher>();
        var capability = Substitute.For<IOutboxCapabilityProvider>();
        capability.SupportsTransactionalOutbox.Returns(true);
        var service = new FeatureMutationEventService(publisher, outboxCapabilityProvider: capability);

        var context = new DefaultHttpContext { TraceIdentifier = "trace-batch" };
        var perOpRequestIds = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["create"] = new[] { "trace-batch:c1", "trace-batch:c2" },
            ["update"] = new[] { "trace-batch:u1" },
        };

        var data = await service.ResolveOutboxScopeAsync(
            context,
            layerId: 7,
            protocol: "OData",
            perOperationRequestIds: perOpRequestIds);

        using var _ = FeatureMutationOutboxScope.BeginIfNotNull(data);
        var feature = Feature.Create(0, geometry: null, ImmutableDictionary<string, object?>.Empty);

        // Each row attempt advances the queue via BeginRowAttempt; EntryFactory then
        // reads the currently bound metadata.
        FeatureMutationOutboxScope.Current!.BeginRowAttempt!("create");
        var firstCreate = FeatureMutationOutboxScope.Current!.EntryFactory(101, "create", feature);
        FeatureMutationOutboxScope.Current!.BeginRowAttempt!("create");
        var secondCreate = FeatureMutationOutboxScope.Current!.EntryFactory(102, "create", feature);
        FeatureMutationOutboxScope.Current!.BeginRowAttempt!("update");
        var firstUpdate = FeatureMutationOutboxScope.Current!.EntryFactory(201, "update", feature);
        // Queue exhausted: should fall back to the parent trace identifier.
        FeatureMutationOutboxScope.Current!.BeginRowAttempt!("create");
        var thirdCreate = FeatureMutationOutboxScope.Current!.EntryFactory(103, "create", feature);
        // No queue for delete: also falls back to the parent trace identifier.
        FeatureMutationOutboxScope.Current!.BeginRowAttempt!("delete");
        var firstDelete = FeatureMutationOutboxScope.Current!.EntryFactory(301, "delete", feature);

        firstCreate!.RequestId.Should().Be("trace-batch:c1");
        secondCreate!.RequestId.Should().Be("trace-batch:c2");
        firstUpdate!.RequestId.Should().Be("trace-batch:u1");
        thirdCreate!.RequestId.Should().Be("trace-batch");
        firstDelete!.RequestId.Should().Be("trace-batch");
    }

    [UnitTest]
    public async Task PublishAsync_WhenOutboxEnabled_SkipsCanonicalPublish()
    {
        // Critical invariant for #692: when the outbox is the system of record for
        // change events, the protocol-layer's post-commit publish must be a no-op.
        // Otherwise consumers see duplicate events (one from the publish path and one
        // from the dispatcher) and the request hot path pays for an unnecessary
        // serialize/append round trip.
        var publisher = Substitute.For<IFeatureChangeEventPublisher>();
        var capability = Substitute.For<IOutboxCapabilityProvider>();
        capability.SupportsTransactionalOutbox.Returns(true);
        var service = new FeatureMutationEventService(publisher, outboxCapabilityProvider: capability);

        var context = new DefaultHttpContext { TraceIdentifier = "trace-skip" };
        await service.PublishAsync(context, layerId: 1, objectId: 1, operation: "create",
            protocol: "Grpc", cancellationToken: CancellationToken.None);

        await publisher.DidNotReceive().PublishAsync(
            Arg.Any<FeatureChangeEventRequest>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task PublishAsync_WhenOutboxDisabled_FallsThroughToCanonicalPublish()
    {
        var publisher = Substitute.For<IFeatureChangeEventPublisher>();
        var capability = Substitute.For<IOutboxCapabilityProvider>();
        capability.SupportsTransactionalOutbox.Returns(false);
        var service = new FeatureMutationEventService(publisher, outboxCapabilityProvider: capability);

        var context = new DefaultHttpContext { TraceIdentifier = "trace-publish" };
        await service.PublishAsync(context, layerId: 1, objectId: 1, operation: "delete",
            protocol: "Grpc", cancellationToken: CancellationToken.None);

        await publisher.Received(1).PublishAsync(
            Arg.Any<FeatureChangeEventRequest>(), Arg.Any<CancellationToken>());
    }
}
