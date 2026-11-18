// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Server.Tests.Features.Protocols.GeoServices;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class GeoServicesWithheldFieldsTests
{
    private static readonly MetadataV2Object Resource = new()
    {
        SchemaFields =
        [
            new() { Name = "objectid", Type = MetadataV2FieldType.Integer },
            new() { Name = "status", Type = MetadataV2FieldType.Integer },
            new() { Name = "salary", Type = MetadataV2FieldType.Double, Hidden = true },
        ]
    };

    [Theory]
    [InlineData("""{"renderer":{"type":"uniqueValue","field1":"SALARY"}}""")]
    [InlineData("""{"renderer":{"type":"classBreaks","field":"status","normalizationField":"salary"}}""")]
    [InlineData("""{"renderer":{"visualVariables":[{"type":"sizeInfo","field":"salary"}]}}""")]
    [InlineData("""{"renderer":{"valueExpression":"$feature.salary > 10"}}""")]
    [InlineData("""{"renderer":{"valueExpression":"$feature['salary']"}}""")]
    [InlineData("""{"title":"Pay {salary}","fieldInfos":[]}""")]
    [InlineData("""{"mediaInfos":[{"type":"barchart","value":{"fields":["salary"]}}]}""")]
    [InlineData("""{"renderer":{"valueExpression":"DomainName($feature, 'salary')"}}""")]
    public void WithoutWithheldReferences_DocumentNamingWithheldField_IsDropped(string json)
    {
        GeoServicesWithheldFields.WithoutWithheldReferences(Parse(json), Resource).Should().BeNull();
    }

    [Theory]
    [InlineData("""{"renderer":{"type":"uniqueValue","field1":"status","uniqueValueInfos":[{"label":"salary band"}]}}""")]
    [InlineData("""{"title":"{status}","labelingInfo":[{"labelExpression":"[status]"}]}""")]
    public void WithoutWithheldReferences_DocumentNamingOnlyVisibleFields_IsKept(string json)
    {
        GeoServicesWithheldFields.WithoutWithheldReferences(Parse(json), Resource).Should().NotBeNull();
    }

    [Fact]
    public void WithoutWithheldReferences_PopupFieldEntryOfWithheldField_IsRemovedAndPopupKept()
    {
        var popup = Parse("""{"title":"{status}","fieldInfos":[{"fieldName":"status"},{"fieldName":"salary","visible":false}]}""");

        var kept = GeoServicesWithheldFields.WithoutWithheldReferences(popup, Resource);

        kept!.Value.GetProperty("fieldInfos").EnumerateArray().Should().ContainSingle()
            .Which.GetProperty("fieldName").GetString().Should().Be("status");
    }

    [Theory]
    [InlineData("""{"labelExpression":"[salary]"}""")]
    [InlineData("""{"labelExpression":"[status]","where":"salary > 100000"}""")]
    public void WithoutWithheldReferences_LabelClassNamingWithheldField_IsRemovedAndRendererKept(string labelClass)
    {
        var drawingInfo = Parse($$"""{"renderer":{"type":"simple"},"labelingInfo":[{{labelClass}}]}""");

        var kept = GeoServicesWithheldFields.WithoutWithheldReferences(drawingInfo, Resource);

        kept!.Value.GetProperty("labelingInfo").GetArrayLength().Should().Be(0);
        kept.Value.GetProperty("renderer").GetProperty("type").GetString().Should().Be("simple");
    }

    [Fact]
    public void ResolveEditFieldsInfo_EveryEditorTrackingFieldWithheld_ReturnsNull()
    {
        var resource = Resource with { Editing = new MetadataV2ObjectEditing { CreatorField = "salary" } };

        FeatureServerEndpoints.ResolveEditFieldsInfo(resource).Should().BeNull();
    }

    [Fact]
    public void WithoutWithheldTimeFields_TimeExtentOverWithheldField_IsOmitted()
    {
        var timeInfo = new FeatureServerTimeInfo { StartTimeField = "salary" };

        GeoServicesWithheldFields.WithoutWithheldTimeFields(Resource, timeInfo).Should().BeNull();
    }

    [Fact]
    public void ResolveEditFieldsInfo_WithheldEditorTrackingField_IsNotNamed()
    {
        var resource = Resource with { Editing = new MetadataV2ObjectEditing { CreatorField = "salary", EditorField = "status" } };

        var info = FeatureServerEndpoints.ResolveEditFieldsInfo(resource);

        info!.CreatorField.Should().BeNull();
        info.EditorField.Should().Be("status");
    }

    [Fact]
    public void ResolveVisibleSubtypes_SubtypeFieldWithheld_ReturnsNull()
    {
        var resource = Resource with { Subtypes = new MetadataV2Subtypes { SubtypeField = "salary" } };

        FeatureServerEndpoints.ResolveVisibleSubtypes(resource).Should().BeNull();
    }

    [Fact]
    public void ResolveVisibleSubtypes_OverrideOfWithheldField_IsRemoved()
    {
        var overrides = new Dictionary<string, MetadataV2SubtypeFieldOverride>
        {
            ["salary"] = new() { DefaultValue = JsonSerializer.SerializeToElement(90000) },
            ["objectid"] = new(),
        };
        var resource = Resource with
        {
            Subtypes = new MetadataV2Subtypes { SubtypeField = "status", Subtypes = [new() { Name = "A", FieldOverrides = overrides }] }
        };

        var subtype = FeatureServerEndpoints.ResolveVisibleSubtypes(resource)!.Subtypes.Should().ContainSingle().Subject;

        subtype.FieldOverrides.Keys.Should().BeEquivalentTo(["objectid"]);
    }

    private static JsonElement Parse(string json) => JsonElement.Parse(json);
}
