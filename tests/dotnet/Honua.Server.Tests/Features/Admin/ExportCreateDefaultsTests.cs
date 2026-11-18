// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Admin.Exports;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Admin;

public sealed class ExportCreateDefaultsTests
{
    [UnitTheory]
    [InlineData("Maui County Parcels", "maui-county-parcels")]
    [InlineData("  __Roads (2026)__ ", "roads-2026")]
    [InlineData("é", "export")]
    [InlineData("ab", "export-ab")]
    public void Slug_LayerName_IsAValidExportId(string name, string expected)
    {
        var slug = ExportCreateDefaults.Slug(name, "fallback");

        slug.Should().Be(expected);
        ExportRequestRules.IsExportId(slug).Should().BeTrue();
    }

    [UnitTheory]
    [InlineData(0, 30, 0, 14)]
    [InlineData(20, 22, 14, 14)]
    [InlineData(null, 8, null, 8)]
    [InlineData(2, null, 2, null)]
    [InlineData(-3, 4, 0, 4)]
    public void ClampZoom_StaysInsideTheTileCeiling(int? min, int? max, int? expectedMin, int? expectedMax)
    {
        var zoom = ExportCreateDefaults.ClampZoom(min, max, 0, 14);

        zoom.MinZoom.Should().Be(expectedMin);
        zoom.MaxZoom.Should().Be(expectedMax);
    }

    [UnitTest]
    public void Resolve_LayerWithNoDeclaredKey_UsesTheObjectIdAsText()
    {
        var resource = Layer("County Roads", Field("name", MetadataV2FieldType.String));
        var plan = ExportCreateDefaults.Resolve(Request(), Snapshot(resource));

        plan.Problem.Should().BeNull();
        plan.Start!.ExportId.Should().Be("county-roads");
        plan.Start.IdentityField.Should().Be("objectid");
        var fields = ExportFields.Resolve(resource, plan.Start.IdentityField!);
        fields.Stable.Should().BeFalse();
        fields.Declared.Single(field => field.Name == "objectid").Type.Should().Be("string");
    }

    [UnitTest]
    public void Resolve_DeclaredTextKey_StaysStableAndOverridable()
    {
        var resource = Layer("Parcels",
            Field("tmk", MetadataV2FieldType.String) with { SemanticRoles = ["id.primary"] },
            Field("owner", MetadataV2FieldType.String));
        var plan = ExportCreateDefaults.Resolve(Request(), Snapshot(resource));

        plan.Start!.IdentityField.Should().Be("tmk");
        ExportFields.Resolve(resource, plan.Start.IdentityField!).Stable.Should().BeTrue();

        var overridden = ExportCreateDefaults.Resolve(Request() with { IdentityField = "owner" }, Snapshot(resource));
        overridden.Start!.IdentityField.Should().Be("owner");
        ExportFields.Resolve(resource, overridden.Start.IdentityField!).Stable.Should().BeFalse();
    }

    private static ExportCreateRequest Request() => new()
    {
        Source = new ExportSourceRequest { Kind = "layer", ServiceId = "roads", LayerId = 3 }
    };

    private static MetadataV2Field Field(string name, MetadataV2FieldType type) => new() { Name = name, Type = type, Nullable = true };

    private static MetadataV2Object Layer(string name, params MetadataV2Field[] fields) => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = "res-roads", Name = name },
        Type = MetadataV2ObjectType.FeatureDataset,
        SchemaFields =
        [
            .. fields,
            new MetadataV2Field { Name = "shape", Type = MetadataV2FieldType.Geometry, Nullable = true, SemanticRoles = ["geometry.primary"] }
        ],
        Spatial = new MetadataV2ObjectSpatial
        {
            SpatialReference = MetadataV2SpatialReference.Wgs84,
            GeometryType = MetadataV2GeometryType.Polygon,
            PrimaryGeometryField = "shape"
        }
    };

    private static MetadataV2GraphSnapshot Snapshot(MetadataV2Object resource)
    {
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "svc-roads", Name = "roads" },
            SpatialReference = MetadataV2SpatialReference.Wgs84
        };
        var publication = new MetadataV2Layer
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "pub-roads", Name = "roads" },
            ServiceId = service.Metadata.Id,
            ResourceId = resource.Metadata.Id,
            Identifier = new MetadataV2LayerIdentifier { Value = "3", IsNumeric = true }
        };
        var graph = new MetadataV2Graph
        {
            Revision = 1,
            Resources = [resource],
            Services = [service],
            Publications = [publication]
        };
        return new MetadataV2GraphSnapshot(graph, "test", DateTimeOffset.Parse("2026-11-18T00:00:00Z", CultureInfo.InvariantCulture));
    }
}
