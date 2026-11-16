// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Core.Tests.Features.Metadata.Domain.V2;

[Protocol(ProtocolNames.TestQuality)]
public sealed class MetadataV2SpatialExtensionsTests
{
    [UnitTheory]
    [Operation(Operations.Metadata)]
    [InlineData("{}")]
    [InlineData("{\"supportedCrs\":null}")]
    public void DeserializeSpatial_MissingOrNullSupportedCrs_UsesEmptyCollection(string json)
    {
        var spatial = JsonSerializer.Deserialize(json, MetadataV2JsonContext.Default.MetadataV2ObjectSpatial)!;
        spatial.SupportedCrs.Should().NotBeNull().And.BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void DeserializeSpatial_ConfiguredSupportedCrs_PreservesCollection()
    {
        var spatial = JsonSerializer.Deserialize("{\"supportedCrs\":[{\"srid\":4326}]}",
            MetadataV2JsonContext.Default.MetadataV2ObjectSpatial)!;
        spatial.SupportedCrs.Should().ContainSingle().Which.Srid.Should().Be(4326);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ReadSrid_NumericSrid_ReturnsValue()
    {
        var resource = new MetadataV2Object
        {
            Spatial = new MetadataV2ObjectSpatial
            {
                SpatialReference = new MetadataV2SpatialReference { Srid = 4326 }
            }
        };
        resource.ReadSrid().Should().Be(4326);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ReadSrid_EpsgCrs_ParsesValue()
    {
        var resource = new MetadataV2Object
        {
            Spatial = new MetadataV2ObjectSpatial
            {
                SpatialReference = new MetadataV2SpatialReference { Crs = "EPSG:3857" }
            }
        };
        resource.ReadSrid().Should().Be(3857);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ReadSrid_Missing_ReturnsNull()
    {
        var resource = new MetadataV2Object();
        resource.ReadSrid().Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ReadBbox_AllFields_ReturnsBox()
    {
        var resource = new MetadataV2Object
        {
            Spatial = new MetadataV2ObjectSpatial
            {
                Bbox = new MetadataV2Bbox { West = -180, South = -90, East = 180, North = 90 }
            }
        };
        var bbox = resource.ReadBbox();
        bbox.Should().NotBeNull();
        bbox!.West.Should().Be(-180);
        bbox.South.Should().Be(-90);
        bbox.East.Should().Be(180);
        bbox.North.Should().Be(90);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ReadBbox_Missing_ReturnsNull()
    {
        var resource = new MetadataV2Object
        {
            Spatial = new MetadataV2ObjectSpatial()
        };
        resource.ReadBbox().Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ReadGeometryType_ReturnsEnum()
    {
        var resource = new MetadataV2Object
        {
            Spatial = new MetadataV2ObjectSpatial { GeometryType = MetadataV2GeometryType.Polygon }
        };
        resource.ReadGeometryType().Should().Be(MetadataV2GeometryType.Polygon);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ReadTemporalFields_AllFields_ReturnsAll()
    {
        var resource = new MetadataV2Object
        {
            Temporal = new MetadataV2ObjectTemporal
            {
                StartTimeField = "ts_start",
                EndTimeField = "ts_end",
                TrackIdField = "track"
            },
        };
        var t = resource.ReadTemporalFields();
        t.StartTimeField.Should().Be("ts_start");
        t.EndTimeField.Should().Be("ts_end");
        t.TrackIdField.Should().Be("track");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void FindPrimaryGeometryField_BySemanticRole_PreferredOverType()
    {
        var resource = new MetadataV2Object
        {
            SchemaFields =
            [
                new MetadataV2Field { Name = "shape", Type = MetadataV2FieldType.Geometry },
                new MetadataV2Field { Name = "geom", Type = MetadataV2FieldType.Binary, SemanticRoles = ["geometry.primary"] },
            ],
        };
        resource.FindPrimaryGeometryField()!.Name.Should().Be("geom");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void FindPrimaryGeometryField_ExplicitName_Wins()
    {
        var resource = new MetadataV2Object
        {
            Spatial = new MetadataV2ObjectSpatial { PrimaryGeometryField = "shape" },
            SchemaFields =
            [
                new MetadataV2Field { Name = "shape", Type = MetadataV2FieldType.Geometry },
                new MetadataV2Field { Name = "geom", Type = MetadataV2FieldType.Geometry, SemanticRoles = ["geometry.primary"] },
            ],
        };
        resource.FindPrimaryGeometryField()!.Name.Should().Be("shape");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void FindPrimaryIdField_ByObjectIdConvention_ReturnsField()
    {
        var resource = new MetadataV2Object
        {
            SchemaFields =
            [
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String },
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer },
            ],
        };
        resource.FindPrimaryIdField()!.Name.Should().Be("objectid");
    }
}
