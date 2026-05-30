// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Infrastructure.Events;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.Events;

[Protocol(TestProtocols.TestQuality)]
public sealed class FeatureMutationEventServiceTests
{
    [UnitTest]
    public async Task PublishAsync_WhenWkbHasNoSridButLayerSridProvided_PublishesGeometryAndCrs()
    {
        // Regression for review finding "Layer-SRID mutation paths drop stream
        // geometry": gRPC ApplyEdits and WFS Transaction publish features whose
        // WKB carries no SRID (default WKBWriter handleSRID:false). The
        // PublishAsync layerSrid fallback must restore the paired
        // geometry/geometryCrs envelope contract for streaming subscribers.
        var publisher = Substitute.For<IFeatureChangeEventPublisher>();
        FeatureChangeEventRequest? captured = null;
        publisher
            .When(p => p.PublishAsync(Arg.Any<FeatureChangeEventRequest>(), Arg.Any<CancellationToken>()))
            .Do(call => captured = call.Arg<FeatureChangeEventRequest>());

        var service = new FeatureMutationEventService(publisher);

        var point = new Point(-157.8583, 21.3069);
        var wkb = new WKBWriter(ByteOrder.LittleEndian, handleSRID: false).Write(point);
        var feature = Feature.Create(42, wkb, ImmutableDictionary<string, object?>.Empty);

        var context = new DefaultHttpContext { TraceIdentifier = "trace-id" };

        await service.PublishAsync(
            context,
            layerId: 0,
            objectId: 42,
            operation: "create",
            protocol: "Grpc",
            CancellationToken.None,
            mutationFeature: feature,
            serviceId: "svc",
            requestId: "req-1",
            layerSrid: 4326);

        captured.Should().NotBeNull();
        captured!.GeometryJson.Should().NotBeNullOrEmpty();
        captured.GeometrySrid.Should().Be(4326);
        captured.GeometryEnvelope.Should().NotBeNull();
    }

    [UnitTest]
    public async Task PublishAsync_WhenWkbHasNoSridAndNoLayerSrid_DropsGeometryToPreserveInvariant()
    {
        // Without a layer SRID fallback the geodesy invariant guard must drop
        // the GeoJSON: clients cannot interpret coordinates without a CRS.
        var publisher = Substitute.For<IFeatureChangeEventPublisher>();
        FeatureChangeEventRequest? captured = null;
        publisher
            .When(p => p.PublishAsync(Arg.Any<FeatureChangeEventRequest>(), Arg.Any<CancellationToken>()))
            .Do(call => captured = call.Arg<FeatureChangeEventRequest>());

        var service = new FeatureMutationEventService(publisher);

        var point = new Point(-157.8583, 21.3069);
        var wkb = new WKBWriter(ByteOrder.LittleEndian, handleSRID: false).Write(point);
        var feature = Feature.Create(42, wkb, ImmutableDictionary<string, object?>.Empty);

        var context = new DefaultHttpContext { TraceIdentifier = "trace-id" };

        await service.PublishAsync(
            context,
            layerId: 0,
            objectId: 42,
            operation: "create",
            protocol: "Grpc",
            CancellationToken.None,
            mutationFeature: feature,
            serviceId: "svc",
            requestId: "req-1");

        captured.Should().NotBeNull();
        captured!.GeometryJson.Should().BeNull();
        captured.GeometrySrid.Should().BeNull();
        // Envelope is still emitted for broadcast-time bbox filter evaluation.
        captured.GeometryEnvelope.Should().NotBeNull();
    }

    [UnitTest]
    public async Task PublishAsync_WhenDeleteEventHasLayerSridButNoMutationFeature_OmitsGeometryAndCrsTogether()
    {
        // Regression: delete events on the gRPC ApplyEdits path do not pass a
        // mutationFeature (no before-image) but do pass layerSrid for the
        // paired-contract fallback. Without a bidirectional guard the else-if
        // would set geometrySrid from layerSrid while leaving geometryJson
        // null, publishing geometryCrs without geometry and breaking the
        // geodesy invariant downstream consumers rely on.
        var publisher = Substitute.For<IFeatureChangeEventPublisher>();
        FeatureChangeEventRequest? captured = null;
        publisher
            .When(p => p.PublishAsync(Arg.Any<FeatureChangeEventRequest>(), Arg.Any<CancellationToken>()))
            .Do(call => captured = call.Arg<FeatureChangeEventRequest>());

        var service = new FeatureMutationEventService(publisher);

        var context = new DefaultHttpContext { TraceIdentifier = "trace-id" };

        await service.PublishAsync(
            context,
            layerId: 0,
            objectId: 42,
            operation: "delete",
            protocol: "Grpc",
            CancellationToken.None,
            serviceId: "svc",
            requestId: "req-3",
            layerSrid: 4326);

        captured.Should().NotBeNull();
        captured!.GeometryJson.Should().BeNull();
        captured.GeometrySrid.Should().BeNull();
    }

    [UnitTest]
    public async Task PublishAsync_WhenCallerSuppliesGeometryJsonButNoSrid_AppliesLayerSridFallback()
    {
        // When a caller pre-enriches geometryJson but cannot resolve the SRID
        // itself, the layerSrid fallback must populate geometrySrid so the
        // downstream paired-contract guard does not strip the GeoJSON.
        var publisher = Substitute.For<IFeatureChangeEventPublisher>();
        FeatureChangeEventRequest? captured = null;
        publisher
            .When(p => p.PublishAsync(Arg.Any<FeatureChangeEventRequest>(), Arg.Any<CancellationToken>()))
            .Do(call => captured = call.Arg<FeatureChangeEventRequest>());

        var service = new FeatureMutationEventService(publisher);

        var context = new DefaultHttpContext { TraceIdentifier = "trace-id" };

        await service.PublishAsync(
            context,
            layerId: 0,
            objectId: 42,
            operation: "update",
            protocol: "Grpc",
            CancellationToken.None,
            serviceId: "svc",
            requestId: "req-2",
            geometryEnvelope: [-157.86, 21.30, -157.85, 21.31],
            propertiesJson: "{}",
            geometryJson: "{\"type\":\"Point\",\"coordinates\":[-157.8583,21.3069]}",
            layerSrid: 4326);

        captured.Should().NotBeNull();
        captured!.GeometryJson.Should().NotBeNullOrEmpty();
        captured.GeometrySrid.Should().Be(4326);
    }
}
