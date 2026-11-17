// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FlatGeobuf;
using Honua.Core.Features.FeatureExport;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Feature = Honua.Core.Features.FeatureStore.Domain.Feature;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class FlatGeobufFeatureWriterGeometryTypeTests
{
    private static readonly GeometryFactory Factory = new(new PrecisionModel(), 4326);

    [Fact]
    public void Write_SingleGeometryTypeLayer_DeclaresThatTypeInTheHeader()
    {
        var payload = WriteLayer(
            Factory.CreatePoint(new Coordinate(1, 2)),
            null,
            Factory.CreatePoint(new Coordinate(3, 4)));

        ReadHeaderGeometryType(payload).Should().Be(GeometryType.Point);
        FlatGeobuf.NTS.FeatureCollectionConversions.Deserialize(payload)
            .Should().HaveCount(3)
            .And.Contain(feature => feature.Geometry is Point);
    }

    [Fact]
    public void Write_MixedGeometryTypeLayer_KeepsTheUnknownHeaderType()
    {
        var payload = WriteLayer(
            Factory.CreatePoint(new Coordinate(1, 2)),
            Factory.CreateLineString([new Coordinate(0, 0), new Coordinate(1, 1)]));

        ReadHeaderGeometryType(payload).Should().Be(GeometryType.Unknown);
    }

    [Fact]
    public void Write_LayerWithoutGeometries_KeepsTheUnknownHeaderType()
    {
        ReadHeaderGeometryType(WriteLayer(null)).Should().Be(GeometryType.Unknown);
    }

    private static byte[] WriteLayer(params Geometry?[] geometries)
    {
        var resource = new MetadataV2Object
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-fgb-type", Name = "fgb-type" },
            SchemaFields = [new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.BigInteger }]
        };
        var writer = new WKBWriter();
        var features = geometries
            .Select((geometry, index) => Feature.Create(index + 1, geometry is null ? null : writer.Write(geometry)))
            .ToArray();
        return FlatGeobufFeatureWriter.Write(features, new FlatGeobufFeatureWriter.ExportOptions
        {
            Resource = resource,
            Query = new FeatureQuery(),
            ProjectedFields = resource.SchemaFields,
            OutputSrid = 4326
        }, CancellationToken.None);
    }

    private static GeometryType ReadHeaderGeometryType(byte[] payload)
    {
        using var stream = new MemoryStream(payload, writable: false);
        return Helpers.ReadHeader(stream).GeometryType;
    }
}
