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
