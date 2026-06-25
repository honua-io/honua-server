// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// Unit tests for the template mapper (#1878): derives the Esri-style layer <c>types</c>
/// (each with an editing template seeded from the subtype's default values) from the
/// canonical Metadata v2 subtype set, mirroring how ArcGIS projects layer types from subtypes.
/// </summary>
public sealed class GeoServicesTemplateMapperTests
{
    private static JsonElement Number(int value)
        => JsonSerializer.SerializeToElement(value);

    [UnitTest]
    public void MapTypes_NullSubtypes_ReturnsNull()
    {
        GeoServicesTemplateMapper.MapTypes(null, "esriGeometryPoint").Should().BeNull();
    }

    [UnitTest]
    public void MapTypes_EmptySubtypes_ReturnsNull()
    {
        var subtypes = new MetadataV2Subtypes { SubtypeField = "subtype", Subtypes = [] };

        GeoServicesTemplateMapper.MapTypes(subtypes, "esriGeometryPoint").Should().BeNull();
    }

    [UnitTest]
    public void MapTypes_DerivesTypePerSubtypeWithSeededTemplate()
    {
        var subtypes = new MetadataV2Subtypes
        {
            SubtypeField = "subtype",
            DefaultSubtypeCode = Number(1),
            Subtypes =
            [
                new MetadataV2Subtype
                {
                    Code = Number(1),
                    Name = "Residential",
                    FieldOverrides = new Dictionary<string, MetadataV2SubtypeFieldOverride>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["material"] = new()
                        {
                            DefaultValue = Number(10),
                            Domain = new MetadataV2FieldDomain
                            {
                                Name = "materials",
                                Type = "codedValue",
                                CodedValues =
                                [
                                    new MetadataV2CodedValue { Code = Number(10), Name = "Copper" },
                                ],
                            },
                        },
                    },
                },
            ],
        };

        var types = GeoServicesTemplateMapper.MapTypes(subtypes, "esriGeometryPolygon");

        types.Should().NotBeNull();
        types!.Should().HaveCount(1);

        var type = types[0];
        type.Id.GetInt32().Should().Be(1);
        type.Name.Should().Be("Residential");

        // Per-subtype domain overrides are surfaced on the type.
        type.Domains.Should().NotBeNull();
        type.Domains!.Should().ContainKey("material");

        // One editing template per type, with the geometry-derived drawing tool.
        type.Templates.Should().HaveCount(1);
        var template = type.Templates[0];
        template.Name.Should().Be("Residential");
        template.DrawingTool.Should().Be("esriFeatureEditToolPolygon");

        var prototype = template.Prototype.Should().BeOfType<FeatureTemplatePrototype>().Subject;
        // Prototype seeds the subtype code under the subtype field plus the field default value.
        prototype.Attributes.Should().ContainKey("subtype");
        prototype.Attributes["subtype"].GetInt32().Should().Be(1);
        prototype.Attributes.Should().ContainKey("material");
        prototype.Attributes["material"].GetInt32().Should().Be(10);
    }

    [UnitTest]
    public void MapTypes_DrawingTool_FollowsGeometryType()
    {
        MetadataV2Subtypes Subtypes() => new()
        {
            SubtypeField = "subtype",
            Subtypes = [new MetadataV2Subtype { Code = Number(1), Name = "A" }],
        };

        GeoServicesTemplateMapper.MapTypes(Subtypes(), "esriGeometryPoint")![0]
            .Templates[0].DrawingTool.Should().Be("esriFeatureEditToolPoint");
        GeoServicesTemplateMapper.MapTypes(Subtypes(), "esriGeometryPolyline")![0]
            .Templates[0].DrawingTool.Should().Be("esriFeatureEditToolLine");
        GeoServicesTemplateMapper.MapTypes(Subtypes(), "esriGeometryPolygon")![0]
            .Templates[0].DrawingTool.Should().Be("esriFeatureEditToolPolygon");
    }
}
