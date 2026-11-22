// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.TestKit.Attributes;
using NSubstitute;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

public partial class GrpcFeatureServiceTests
{
    [UnitTest]
    [Endpoint("POST /grpc/geospatial.v1.FeatureService/QueryFeatures")]
    public async Task QueryFeatures_WithAliasOutSrAndLatestWkid_EchoesRequestedWkidAndCanonicalLatestWkid()
    {
        var features = ImmutableArray.Create(Feature.Create(1, null));
        _featureReader.QueryAsync(0, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(QueryResult<Feature>.Create(1, features));

        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = "test",
            LayerId = 0,
            OutSr = new Proto.SpatialReference { Wkid = 102100, LatestWkid = 3857 }
        };

        using var callContext = CreateCallContext();
        var response = await _sut.QueryFeatures(request, callContext);

        response.SpatialReference.Wkid.Should().Be(102100);
        response.SpatialReference.LatestWkid.Should().Be(3857);
        await _featureReader.Received(1).QueryAsync(
            0,
            Arg.Is<FeatureQuery>(q => q.OutputSrid == 3857),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Endpoint("POST /grpc/geospatial.v1.FeatureService/QueryFeatures")]
    public async Task QueryFeatures_WithWebMercatorAliasWkidOnlyOutSr_EchoesRequestedWkidAndCanonicalLatestWkid()
    {
        var features = ImmutableArray.Create(Feature.Create(1, null));
        _featureReader.QueryAsync(0, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(QueryResult<Feature>.Create(1, features));

        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = "test",
            LayerId = 0,
            OutSr = new Proto.SpatialReference { Wkid = 102100 }
        };

        using var callContext = CreateCallContext();
        var response = await _sut.QueryFeatures(request, callContext);

        response.SpatialReference.Wkid.Should().Be(102100);
        response.SpatialReference.LatestWkid.Should().Be(3857);
        await _featureReader.Received(1).QueryAsync(
            0,
            Arg.Is<FeatureQuery>(q => q.OutputSrid == 3857),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Endpoint("POST /grpc/geospatial.v1.FeatureService/QueryFeatures")]
    public async Task QueryFeatures_WithConflictingWkidAndLatestWkidOutSr_DoesNotEchoMismatchedWkid()
    {
        var features = ImmutableArray.Create(Feature.Create(1, null));
        _featureReader.QueryAsync(0, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(QueryResult<Feature>.Create(1, features));

        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = "test",
            LayerId = 0,
            OutSr = new Proto.SpatialReference { Wkid = 4326, LatestWkid = 3857 }
        };

        using var callContext = CreateCallContext();
        var response = await _sut.QueryFeatures(request, callContext);

        response.SpatialReference.Wkid.Should().Be(3857);
        response.SpatialReference.LatestWkid.Should().Be(3857);
        await _featureReader.Received(1).QueryAsync(
            0,
            Arg.Is<FeatureQuery>(q => q.OutputSrid == 3857),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Endpoint("POST /grpc/geospatial.v1.FeatureService/QueryFeaturesStream")]
    public async Task QueryFeaturesStream_WithAliasOutSrAndLatestWkid_EchoesRequestedWkidAndCanonicalLatestWkid()
    {
        var features = new[] { Feature.Create(1, null) }.ToAsyncEnumerable();
        _streamingStore.StreamFeaturesAsync(0, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(features);

        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = "test",
            LayerId = 0,
            OutSr = new Proto.SpatialReference { Wkid = 102100, LatestWkid = 3857 }
        };

        var writer = new TestServerStreamWriter<Proto.FeaturePage>();
        using var callContext = CreateCallContext();
        await _sut.QueryFeaturesStream(request, writer, callContext);

        writer.Pages.Should().ContainSingle();
        writer.Pages[0].SpatialReference.Wkid.Should().Be(102100);
        writer.Pages[0].SpatialReference.LatestWkid.Should().Be(3857);
        _streamingStore.Received(1).StreamFeaturesAsync(
            0,
            Arg.Is<FeatureQuery>(q => q.OutputSrid == 3857),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Endpoint("POST /grpc/geospatial.v1.FeatureService/QueryFeaturesStream")]
    public async Task QueryFeaturesStream_WithWebMercatorAliasWkidOnlyOutSr_EchoesRequestedWkidAndCanonicalLatestWkid()
    {
        var features = new[] { Feature.Create(1, null) }.ToAsyncEnumerable();
        _streamingStore.StreamFeaturesAsync(0, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(features);

        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = "test",
            LayerId = 0,
            OutSr = new Proto.SpatialReference { Wkid = 102100 }
        };

        var writer = new TestServerStreamWriter<Proto.FeaturePage>();
        using var callContext = CreateCallContext();
        await _sut.QueryFeaturesStream(request, writer, callContext);

        writer.Pages.Should().ContainSingle();
        writer.Pages[0].SpatialReference.Wkid.Should().Be(102100);
        writer.Pages[0].SpatialReference.LatestWkid.Should().Be(3857);
        _streamingStore.Received(1).StreamFeaturesAsync(
            0,
            Arg.Is<FeatureQuery>(q => q.OutputSrid == 3857),
            Arg.Any<CancellationToken>());
    }
}
