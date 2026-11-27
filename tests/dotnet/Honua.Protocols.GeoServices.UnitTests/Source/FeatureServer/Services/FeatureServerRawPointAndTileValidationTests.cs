// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Tiles;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Protocols.GeoServices.UnitTests.Source.FeatureServer.Services;

[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerRawPointAndTileValidationTests
{
    [UnitTest]
    [Operation(Operations.Query)]
    public void CreateRawGeoServicesPointPayload_NonFiniteCoordinates_WritesNullGeometry()
    {
        var resource = new MetadataV2Object
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-points", Name = "points" },
            Spatial = new MetadataV2ObjectSpatial
            {
                GeometryType = MetadataV2GeometryType.Point,
                SpatialReference = MetadataV2SpatialReference.Wgs84
            },
            SchemaFields =
            [
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.BigInteger, Nullable = false }
            ]
        };
        var result = PagedQueryResult<RawGeoServicesFeature>.Create(
            ImmutableArray.Create(
                RawGeoServicesFeature.Create(1, "{}", double.NaN, 37.7),
                RawGeoServicesFeature.Create(2, "{}", -122.4, double.PositiveInfinity),
                RawGeoServicesFeature.Create(3, "{}", -122.4, 37.7)),
            hasMoreResults: false);

        var payload = FeatureServerQueryExecutor.CreateRawGeoServicesPointPayload(
            result,
            resource,
            returnGeometry: true,
            outputSrid: 4326);

        using var document = JsonDocument.Parse(Encoding.UTF8.GetString(payload.Span));
        var features = document.RootElement.GetProperty("features");
        features.GetArrayLength().Should().Be(3);
        features[0].GetProperty("geometry").ValueKind.Should().Be(JsonValueKind.Null);
        features[1].GetProperty("geometry").ValueKind.Should().Be(JsonValueKind.Null);
        features[2].GetProperty("geometry").GetProperty("x").GetDouble().Should().Be(-122.4);
        features[2].GetProperty("geometry").GetProperty("y").GetDouble().Should().Be(37.7);
    }

    [UnitTheory]
    [InlineData(-1, 0, 0, false)]
    [InlineData(0, 0, -1, false)]
    [InlineData(0, 0, 25, false)]
    [InlineData(0, 0, 31, false)]
    [InlineData(0, 0, 32, false)]
    [InlineData(1, 0, 0, false)]
    [InlineData(0, 0, 0, true)]
    [InlineData(3, 3, 2, true)]
    [Operation(Operations.Tile)]
    public void ValidateTileCoordinates_RejectsOverflowAndOutOfBoundsCoordinates(int x, int y, int z, bool expected)
    {
        TileMath.ValidateTileCoordinates(x, y, z).Should().Be(expected);
    }
}
