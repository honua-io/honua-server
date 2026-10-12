// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Models;

/// <summary>
/// Pins the FeatureServer layer descriptor's data-model surface (#1878): the layer-level
/// <c>contingentValuesDefinition</c> and subtype-derived <c>types</c> are emitted when the
/// layer authors them, and omitted (byte-stable for existing Esri clients) when it does not.
/// </summary>
public sealed class LayerResponseDataModelSerializationTests
{
    private static JsonElement Number(int value) => JsonSerializer.SerializeToElement(value);

    private static LayerResponse BuildLayer(
        ContingentValuesDefinition? contingent = null,
        GeoServicesLayerType[]? types = null) => new()
        {
            Id = 0,
            Name = "test",
            GeometryType = "esriGeometryPoint",
            SpatialReference = new SpatialReferenceInfo { Wkid = 4326, LatestWkid = 4326 },
            Fields = [],
            ObjectIdField = "objectid",
            ContingentValuesDefinition = contingent,
            Types = types,
        };

    [Fact]
    public void Serialize_WhenNoDataModel_OmitsContingentValuesAndTypes()
    {
        var json = JsonSerializer.Serialize(BuildLayer(), FeatureServerJsonContext.Default.LayerResponse);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("contingentValuesDefinition", out _).Should().BeFalse(
            "layers without contingent values must omit the definition to stay byte-stable");
        doc.RootElement.TryGetProperty("types", out _).Should().BeFalse(
            "layers without subtypes must omit types to stay byte-stable");
    }

    [Fact]
    public void Serialize_WhenContingentValuesAuthored_EmitsDefinition()
    {
        var contingent = new ContingentValuesDefinition
        {
            Id = 0,
            FieldGroups =
            [
                new ContingentValueFieldGroup
                {
                    Name = "material-diameter",
                    Restrictive = true,
                    Fields = ["material", "diameter"],
                    ContingentValues =
                    [
                        new ContingentValueRow
                        {
                            Id = 1,
                            Values = new(StringComparer.OrdinalIgnoreCase)
                            {
                                ["material"] = new ContingentFieldValue { Type = "code", Code = Number(10) },
                            },
                        },
                    ],
                },
            ],
        };

        var json = JsonSerializer.Serialize(BuildLayer(contingent: contingent), FeatureServerJsonContext.Default.LayerResponse);

        using var doc = JsonDocument.Parse(json);
        var definition = doc.RootElement.GetProperty("contingentValuesDefinition");
        var group = definition.GetProperty("fieldGroups")[0];
        group.GetProperty("name").GetString().Should().Be("material-diameter");
        group.GetProperty("fields").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("material", "diameter");
    }

    [Fact]
    public void Serialize_WhenSubtypesAuthored_EmitsTypesWithTemplates()
    {
        var types = new[]
        {
            new GeoServicesLayerType
            {
                Id = Number(1),
                Name = "Residential",
                Templates =
                [
                    new FeatureTemplate
                    {
                        Name = "Residential",
                        DrawingTool = "esriFeatureEditToolPoint",
                        Prototype = new FeatureTemplatePrototype
                        {
                            Attributes = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["subtype"] = Number(1),
                            },
                        },
                    },
                ],
            },
        };

        var json = JsonSerializer.Serialize(BuildLayer(types: types), FeatureServerJsonContext.Default.LayerResponse);

        using var doc = JsonDocument.Parse(json);
        var type = doc.RootElement.GetProperty("types")[0];
        type.GetProperty("id").GetInt32().Should().Be(1);
        type.GetProperty("name").GetString().Should().Be("Residential");

        var template = type.GetProperty("templates")[0];
        template.GetProperty("name").GetString().Should().Be("Residential");
        template.GetProperty("prototype").GetProperty("attributes").GetProperty("subtype").GetInt32().Should().Be(1);
    }
}
