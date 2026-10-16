// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Scene.Domain;
using Honua.Db.Postgres.Features.Admin;

namespace Honua.Db.Postgres.Tests.Features.Import;

[Trait("Tier", "Fast")]
public sealed class PublishedLayerFieldRewriteTests
{
    [Fact]
    public void ApplyReplacementFieldMetadata_DropsDanglingReferencesAndKeepsTheNewSchema()
    {
        var now = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
        var statusDomain = new MetadataV2FieldDomain
        {
            Name = "Status",
            Type = "codedValue",
            CodedValues = [new MetadataV2CodedValue { Name = "Open", Code = JsonSerializer.SerializeToElement(1) }]
        };
        var schemaFields = new MetadataV2Field[]
        {
            new() { Name = "objectid", Type = MetadataV2FieldType.Integer },
            new() { Name = "name", Type = MetadataV2FieldType.String },
            new() { Name = "status", Type = MetadataV2FieldType.String, Domain = statusDomain },
            new() { Name = "observed", Type = MetadataV2FieldType.String },
            new() { Name = "global_id", Type = MetadataV2FieldType.Uuid },
            new() { Name = "geom", Type = MetadataV2FieldType.Geometry }
        };
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = "res-layer-7",
                Name = "parcels",
                UpdatedAt = DateTimeOffset.UnixEpoch
            },
            Type = MetadataV2ResourceType.FeatureDataset,
            SchemaFields = [new MetadataV2Field { Name = "zoning", Type = MetadataV2FieldType.String }],
            Display = new MetadataV2ResourceDisplay { DisplayField = "zoning", HasZ = false, HasM = false },
            Spatial = new MetadataV2ResourceSpatial
            {
                PrimaryGeometryField = "shape",
                GeometryType = MetadataV2GeometryType.Point
            },
            Temporal = new MetadataV2ResourceTemporal { StartTimeField = "observed", EndTimeField = "zoning" },
            Editing = new MetadataV2ResourceEditing
            {
                GlobalIdField = "global_id",
                CreatorField = "created_by",
                CanModify = true,
                SupportsAttachments = true,
                SupportsRelatedRecords = true
            },
            Subtypes = new MetadataV2Subtypes
            {
                SubtypeField = "zoning",
                Subtypes = [new MetadataV2Subtype { Name = "Residential", Code = JsonSerializer.SerializeToElement(1) }]
            },
            Symbology3D = new Symbology3D
            {
                Rules =
                [
                    new Symbology3DRule { Attribute = "zoning", Value = "1" },
                    new Symbology3DRule { Attribute = "name", Value = "Beta" }
                ]
            },
            Extrusion = new MetadataV2ExtrusionInfo { HeightField = "zoning", Unit = "meters" }
        };
        var request = new LayerPublishRequest
        {
            Schema = "honua_data",
            Table = "parcels",
            LayerName = "Parcels",
            GeometryColumn = "geom",
            GeometryType = "MULTIPOLYGON",
            HasZ = true,
            HasM = true,
            PrimaryKey = "objectid",
            GlobalIdField = "global_id",
            SupportsAttachments = false,
            Subtypes = resource.Subtypes,
            AttributeRules =
            [
                new MetadataV2AttributeRule
                {
                    Name = "calc-zoning",
                    Type = MetadataV2AttributeRuleType.Calculation,
                    FieldName = "zoning",
                    ScriptExpression = "1"
                },
                new MetadataV2AttributeRule
                {
                    Name = "calc-name",
                    Type = MetadataV2AttributeRuleType.Calculation,
                    FieldName = "name",
                    ScriptExpression = "\"x\""
                }
            ]
        };

        var updated = PostgreSqlLayerPublishingService.ApplyReplacementFieldMetadata(
            resource, schemaFields, request, "objectid", "geom", "MULTIPOLYGON", now);

        updated.Metadata.Id.Should().Be("res-layer-7");
        updated.Metadata.UpdatedAt.Should().Be(now);
        updated.Type.Should().Be(MetadataV2ResourceType.FeatureDataset);
        updated.SchemaFields.Select(field => field.Name).Should().Equal("objectid", "name", "status", "observed", "global_id", "geom");
        updated.SchemaFields.Should().NotContain(field => field.Name == "zoning");
        updated.SchemaFields.Single(field => field.Name == "status").Domain.Should().BeSameAs(statusDomain);
        updated.Display!.DisplayField.Should().Be("name");
        updated.Display.HasZ.Should().BeTrue();
        updated.Display.HasM.Should().BeTrue();
        updated.Spatial!.PrimaryGeometryField.Should().Be("geom");
        updated.Spatial.GeometryType.Should().Be(MetadataV2GeometryType.MultiPolygon);
        updated.Temporal.Should().BeNull();
        updated.Subtypes.Should().BeNull();
        updated.Extrusion.Should().BeNull();
        updated.Symbology3D!.Rules.Should().ContainSingle().Which.Attribute.Should().Be("name");
        updated.AttributeRules.Should().ContainSingle().Which.Name.Should().Be("calc-name");
        updated.Editing!.GlobalIdField.Should().Be("global_id");
        updated.Editing.CreatorField.Should().BeNull();
        updated.Editing.CanModify.Should().BeTrue();
        updated.Editing.SupportsAttachments.Should().BeTrue();
        updated.Editing.SupportsRelatedRecords.Should().BeTrue();
    }
}
