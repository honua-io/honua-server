// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Apache.Arrow;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Protocols.OData;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Tests.Features.Protocols.OData;

/// <summary>
/// OData <c>$format=parquet</c> geometry fidelity. The JSON geometry payload keeps Z.
/// The Parquet page must keep the same ordinate.
/// </summary>
public sealed class ODataGeoParquetExportTests
{
    [Fact]
    public async Task FormatPage_PointWithZ_PreservesZ()
    {
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "test-layer", Name = "test_layer" },
            SchemaFields =
            [
                new MetadataV2Field
                {
                    Name = FieldNames.ObjectId,
                    Type = MetadataV2FieldType.BigInteger,
                    Nullable = false
                },
                new MetadataV2Field
                {
                    Name = "shape",
                    Type = MetadataV2FieldType.Geometry,
                    Nullable = true,
                    SemanticRoles = ["geometry.primary"]
                }
            ],
            Spatial = new MetadataV2ResourceSpatial
            {
                SpatialReference = MetadataV2SpatialReference.Wgs84,
                GeometryType = MetadataV2GeometryType.Point,
                PrimaryGeometryField = "shape"
            }
        };
        var wkb = new WKBWriter(ByteOrder.LittleEndian, handleSRID: false, emitZ: true, emitM: false)
            .Write(new Point(new CoordinateZ(1.25, 2.5, 3.75)) { SRID = 4326 });
        var feature = Feature.Create(
            1,
            wkb,
            new Dictionary<string, object?> { [FieldNames.ObjectId] = 1L }.ToImmutableDictionary());

        var (payload, _) = ODataGeoParquetExport.FormatPage(
            QueryResult<Feature>.Create(1, [feature]),
            resource,
            new GeometryLimits());

        using var stream = new MemoryStream(payload);
        using var reader = new ParquetSharp.Arrow.FileReader(stream);
        using var batchReader = reader.GetRecordBatchReader();
        var batch = await batchReader.ReadNextRecordBatchAsync();
        batch.Should().NotBeNull();
        var geometry = (BinaryArray)batch!.Column("geometry");
        var point = (Point)new WKBReader().Read(geometry.GetBytes(0).ToArray());
        point.Z.Should().Be(3.75);
        point.X.Should().Be(1.25);
        point.Y.Should().Be(2.5);
    }
}
