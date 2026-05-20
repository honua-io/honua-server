// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Core.Tests.Features.Metadata.Domain.V2;

[Protocol(Protocols.TestQuality)]
public sealed class MetadataV2SpatialExtensionsTests
{
    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ReadSrid_NumericSrid_ReturnsValue()
    {
        var resource = ResourceWithSpatial("""{"srid": 4326}""");
        resource.ReadSrid().Should().Be(4326);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ReadSrid_EpsgCrs_ParsesValue()
    {
        var resource = ResourceWithSpatial("""{"crs": "EPSG:3857"}""");
        resource.ReadSrid().Should().Be(3857);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ReadSrid_Missing_ReturnsNull()
    {
        var resource = new MetadataV2Resource();
        resource.ReadSrid().Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ReadBbox_AllFields_ReturnsBox()
    {
        var resource = ResourceWithSpatial("""{"bbox": {"west": -180, "south": -90, "east": 180, "north": 90}}""");
        var bbox = resource.ReadBbox();
        bbox.Should().NotBeNull();
        bbox!.Value.West.Should().Be(-180);
        bbox.Value.South.Should().Be(-90);
        bbox.Value.East.Should().Be(180);
        bbox.Value.North.Should().Be(90);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ReadBbox_PartialBox_ReturnsNull()
    {
        var resource = ResourceWithSpatial("""{"bbox": {"west": -180, "south": -90}}""");
        resource.ReadBbox().Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ReadGeometryType_ReturnsString()
    {
        var resource = ResourceWithSpatial("""{"geometryType": "Polygon"}""");
        resource.ReadGeometryType().Should().Be("Polygon");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ReadTemporalFields_AllFields_ReturnsAll()
    {
        var resource = new MetadataV2Resource
        {
            Temporal = JsonSerializer.Deserialize<JsonElement>("""{"startTimeField": "ts_start", "endTimeField": "ts_end", "trackIdField": "track"}"""),
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
        var resource = new MetadataV2Resource
        {
            SchemaFields =
            [
                new MetadataV2Field { Name = "shape", Type = "geometry" },
                new MetadataV2Field { Name = "geom", Type = "wkb", SemanticRoles = ["geometry.primary"] },
            ],
        };
        resource.FindPrimaryGeometryField()!.Name.Should().Be("geom");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void FindPrimaryIdField_ByObjectIdConvention_ReturnsField()
    {
        var resource = new MetadataV2Resource
        {
            SchemaFields =
            [
                new MetadataV2Field { Name = "name", Type = "string" },
                new MetadataV2Field { Name = "objectid", Type = "int32" },
            ],
        };
        resource.FindPrimaryIdField()!.Name.Should().Be("objectid");
    }

    private static MetadataV2Resource ResourceWithSpatial(string json)
        => new() { Spatial = JsonSerializer.Deserialize<JsonElement>(json) };
}
