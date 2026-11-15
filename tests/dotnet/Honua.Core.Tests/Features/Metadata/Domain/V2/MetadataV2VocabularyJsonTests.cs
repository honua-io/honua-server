// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Metadata.Domain.V2;

public sealed class MetadataV2VocabularyJsonTests
{
    [UnitTest]
    public void Read_LegacyOnlyGraph_AbsentCanonicalAliasesDoNotEraseValues()
    {
        var graph = Read("""
            {"resources":[{"metadata":{"id":"current"},"type":"map",
              "styleResourceIds":["style"],"relationships":[{"relatedResourceId":"related"}]}],
             "publications":[{"resourceId":"current","publicationType":"future-layer"}],
             "storageBindings":[{"resourceId":"current"}],
             "services":[{"publicationIds":["layer"]}]}
            """);

        graph.Objects.Single().Metadata.Id.Should().Be("current");
        graph.Objects.Single().Type.Should().Be(MetadataV2ObjectType.Map);
        graph.Objects.Single().StyleObjectIds.Should().Equal("style");
        graph.Objects.Single().Relationships.Single().RelatedObjectId.Should().Be("related");
        graph.Layers.Single().ObjectId.Should().Be("current");
        graph.StorageBindings.Single().ObjectId.Should().Be("current");
        graph.Services.Single().LayerIds.Should().Equal("layer");
        var saved = JsonNode.Parse(JsonSerializer.Serialize(graph, MetadataV2JsonContext.Default.MetadataV2Graph))!;
        saved["layers"]![0]!["layerType"]!.GetValue<string>().Should().Be("future-layer");
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_MixedVocabulary_LegacyEditsWinInEitherPropertyOrder(bool canonicalFirst)
    {
        var legacy = "\"resources\":[{\"metadata\":{\"id\":\"current\"}}],\"publications\":[{\"resourceId\":\"current\"}]";
        var canonical = "\"objects\":[{\"metadata\":{\"id\":\"stale\"}}],\"layers\":[{\"objectId\":\"stale\"}]";
        var json = "{" + (canonicalFirst ? canonical + "," + legacy : legacy + "," + canonical) + "}";
        var graph = Read(json);

        graph.Objects.Single().Metadata.Id.Should().Be("current");
        graph.Layers.Single().ObjectId.Should().Be("current");
        var saved = JsonNode.Parse(JsonSerializer.Serialize(graph, MetadataV2JsonContext.Default.MetadataV2Graph))!;
        saved["objects"]![0]!["metadata"]!["id"]!.GetValue<string>().Should().Be("current");
        saved["layers"]![0]!["objectId"]!.GetValue<string>().Should().Be("current");
    }

    [UnitTheory]
    [InlineData("[]")]
    [InlineData("null")]
    public void Read_ExplicitEmptyLegacyCollection_DoesNotResurrectStaleAlias(string legacy)
    {
        Read("{\"resources\":" + legacy + ",\"objects\":[{}]}").Objects.Should().BeEmpty();
        Read("{\"objects\":[{}],\"resources\":" + legacy + "}").Objects.Should().BeEmpty();
    }

    [UnitTest]
    public void Read_CanonicalOnlyGraph_WritesBothVocabulariesAndRetainsKindsAndExtensions()
    {
        var graph = Read("""
            {"objects":[{"metadata":{"id":"map"},"type":"map","future":{"enabled":true}}],
             "layers":[{"objectId":"map"}], "storageBindings":[{"objectId":"map"}],
             "services":[{"layerIds":["layer"]}]}
            """);
        graph.Resources.Single().Type.Should().Be(MetadataV2ObjectType.Map);
        graph.Publications.Single().ResourceId.Should().Be("map");
        graph.StorageBindings.Single().ResourceId.Should().Be("map");
        graph.Services.Single().PublicationIds.Should().Equal("layer");
        var saved = JsonNode.Parse(JsonSerializer.Serialize(graph, MetadataV2JsonContext.Default.MetadataV2Graph))!;
        saved["resources"]![0]!["future"]!["enabled"]!.GetValue<bool>().Should().BeTrue();
        saved["objects"]![0]!["type"]!.GetValue<string>().Should().Be("map");
        saved["publications"]![0]!["resourceId"]!.GetValue<string>().Should().Be("map");
    }

    [UnitTest]
    public void Read_LegacyWriterPreservesStaleAliases_NewSaveRegeneratesAliases()
    {
        var graph = Read("""
            {"resources":[{"metadata":{"id":"before"},"type":"style"}],"publications":[]}
            """);
        var document = JsonNode.Parse(JsonSerializer.Serialize(graph, MetadataV2JsonContext.Default.MetadataV2Graph))!;
        // Model rc.2's extension-data behavior: it edits its known field and preserves the alias.
        document["resources"]![0]!["metadata"]!["id"] = "after";
        var edited = Read(document.ToJsonString());
        edited.Objects.Single().Metadata.Id.Should().Be("after");
        var saved = JsonNode.Parse(JsonSerializer.Serialize(edited, MetadataV2JsonContext.Default.MetadataV2Graph))!;
        saved["objects"]![0]!["metadata"]!["id"]!.GetValue<string>().Should().Be("after");
        saved["objects"]![0]!["type"]!.GetValue<string>().Should().Be("style");
    }

    [UnitTest]
    public void With_CanonicalEditAfterRead_UpdatesValueWithoutMutatingSource()
    {
        var graph = Read("{\"resources\":[{\"metadata\":{\"id\":\"before\"}}]}");
        var edited = graph with { Objects = [] };
        edited.Resources.Should().BeEmpty();
        graph.Resources.Should().ContainSingle();
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_NestedLegacyEdits_ReplaceStaleIdsStylesAndLayerTypes(bool canonicalFirst)
    {
        static string Pair(string legacy, string canonical, bool first) =>
            "{" + (first ? canonical + "," + legacy : legacy + "," + canonical) + "}";

        var relationshipJson = Pair("\"relatedResourceId\":\"current\"", "\"relatedObjectId\":\"stale\"", canonicalFirst);
        var objectJson = Pair("\"styleResourceIds\":[],\"relationships\":[" + relationshipJson + "]",
            "\"styleObjectIds\":[\"stale\"]", canonicalFirst);
        var layerJson = Pair("\"resourceId\":\"current\",\"publicationType\":\"future-layer\"",
            "\"objectId\":\"stale\",\"layerType\":\"stale-layer\"", canonicalFirst);
        var bindingJson = Pair("\"resourceId\":\"current\"", "\"objectId\":\"stale\"", canonicalFirst);
        var serviceJson = Pair("\"publicationIds\":[]", "\"layerIds\":[\"stale\"]", canonicalFirst);
        var graph = Read("{\"resources\":[" + objectJson + "],\"publications\":[" + layerJson
            + "],\"storageBindings\":[" + bindingJson + "],\"services\":[" + serviceJson + "]}");

        graph.Objects.Single().StyleObjectIds.Should().BeEmpty();
        graph.Objects.Single().Relationships.Single().RelatedObjectId.Should().Be("current");
        graph.Layers.Single().ObjectId.Should().Be("current");
        graph.StorageBindings.Single().ObjectId.Should().Be("current");
        graph.Services.Single().LayerIds.Should().BeEmpty();
        var saved = JsonNode.Parse(JsonSerializer.Serialize(graph, MetadataV2JsonContext.Default.MetadataV2Graph))!;
        saved["layers"]![0]!["layerType"]!.GetValue<string>().Should().Be("future-layer");
        saved["layers"]![0]!["publicationType"]!.GetValue<string>().Should().Be("future-layer");
    }

    [UnitTheory]
    [InlineData("external-object", "external-resource")]
    [InlineData("future-object-kind", "future-object-kind")]
    public void Read_CanonicalObjectKind_KeepsLegacyTokenAndUnknownKinds(string canonical, string legacy)
    {
        var graph = Read("{\"objects\":[{\"objectType\":\"" + canonical + "\"}]}");
        var saved = JsonNode.Parse(JsonSerializer.Serialize(graph, MetadataV2JsonContext.Default.MetadataV2Graph))!;
        saved["resources"]![0]!["type"]!.GetValue<string>().Should().Be(legacy);
        saved["objects"]![0]!["objectType"]!.GetValue<string>().Should().Be(canonical);
        if (canonical == "external-object")
        {
            graph.Objects.Single().Type.Should().Be(MetadataV2ObjectType.ExternalResource);
        }
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_MixedObjectKinds_LegacyEditReplacesStaleCanonicalKind(bool canonicalFirst)
    {
        const string legacy = "\"type\":\"map\"";
        const string canonical = "\"objectType\":\"external-object\"";
        var properties = canonicalFirst ? canonical + "," + legacy : legacy + "," + canonical;
        var graph = Read("{\"objects\":[{" + properties + "}]}");

        graph.Objects.Single().Type.Should().Be(MetadataV2ObjectType.Map);
        var saved = JsonNode.Parse(JsonSerializer.Serialize(graph, MetadataV2JsonContext.Default.MetadataV2Graph))!;
        saved["objects"]![0]!["objectType"]!.GetValue<string>().Should().Be("map");
    }

    [UnitTheory]
    [InlineData("\"resourceId\":null,\"objectId\":\"stale\"")]
    [InlineData("\"objectId\":\"stale\",\"resourceId\":null")]
    public void Read_ExplicitNullLegacyReference_DoesNotResurrectStaleAlias(string properties)
    {
        var graph = Read("{\"storageBindings\":[{" + properties + "}],\"layers\":[{" + properties + "}]}");
        graph.StorageBindings.Single().ObjectId.Should().BeEmpty();
        graph.Layers.Single().ObjectId.Should().BeEmpty();
    }

    private static MetadataV2Graph Read(string json) =>
        JsonSerializer.Deserialize(json, MetadataV2JsonContext.Default.MetadataV2Graph)!;
}
