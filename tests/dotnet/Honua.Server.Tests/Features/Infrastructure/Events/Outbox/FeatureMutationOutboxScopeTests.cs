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
    public async Task ResolveOutboxScopeAsync_SnapshotWithGeometry_EncodesGeometryChangedTrue()
    {
        // GeometryChanged regression guard: outbox-built FeatureChangeEventRequest must
        // mirror the protocol-layer publish heuristic so streaming/webhook consumers can
        // distinguish geometry-touching mutations from attribute-only updates after the
        // outbox dispatcher publishes.
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
            layerSrid: 4326);

        using var _ = FeatureMutationOutboxScope.BeginIfNotNull(data);
        var withGeometry = Feature.Create(11, geometry: new byte[] { 0x01, 0x02 }, ImmutableDictionary<string, object?>.Empty);
        var entry = FeatureMutationOutboxScope.Current!.EntryFactory(11, "update", withGeometry);

        entry.Should().NotBeNull();
        entry!.EventPayload.Should().Contain("\"GeometryChanged\":true");
    }

    [UnitTest]
    public async Task ResolveOutboxScopeAsync_SnapshotWithoutGeometry_EncodesGeometryChangedFalse()
    {
        var publisher = Substitute.For<IFeatureChangeEventPublisher>();
        var capability = Substitute.For<IOutboxCapabilityProvider>();
        capability.SupportsTransactionalOutbox.Returns(true);
        var service = new FeatureMutationEventService(publisher, outboxCapabilityProvider: capability);

        var context = new DefaultHttpContext { TraceIdentifier = "trace-attr" };
        var data = await service.ResolveOutboxScopeAsync(
            context,
            layerId: 7,
            protocol: "OgcFeatures",
            requestId: "req-attr");

        using var _ = FeatureMutationOutboxScope.BeginIfNotNull(data);
        var attributesOnly = Feature.Create(12, geometry: null, ImmutableDictionary<string, object?>.Empty);
        var entry = FeatureMutationOutboxScope.Current!.EntryFactory(12, "update", attributesOnly);

        entry.Should().NotBeNull();
        entry!.EventPayload.Should().Contain("\"GeometryChanged\":false");
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

        var firstCreate = FeatureMutationOutboxScope.Current!.EntryFactory(101, "create", feature);
        var secondCreate = FeatureMutationOutboxScope.Current!.EntryFactory(102, "create", feature);
        var firstUpdate = FeatureMutationOutboxScope.Current!.EntryFactory(201, "update", feature);
        // Queue exhausted: should fall back to the parent trace identifier.
        var thirdCreate = FeatureMutationOutboxScope.Current!.EntryFactory(103, "create", feature);
        // No queue for delete: also falls back to the parent trace identifier.
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
