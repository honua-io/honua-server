// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Core.Tests.Features.Metadata.Domain.V2;

[Protocol(ProtocolNames.TestQuality)]
public sealed class MetadataV2VocabularyTests
{
    [UnitTheory]
    [Operation(Operations.Metadata)]
    [InlineData("MetadataV2Object")]
    [InlineData("MetadataV2ObjectType")]
    [InlineData("MetadataV2ObjectDisplay")]
    [InlineData("MetadataV2ObjectEditing")]
    [InlineData("MetadataV2ObjectSpatial")]
    [InlineData("MetadataV2ObjectTemporal")]
    [InlineData("MetadataV2Style")]
    [InlineData("MetadataV2StyleFactory")]
    public void Assembly_DeclaresCanonicalObjectTypes(string typeName)
    {
        typeof(MetadataV2Graph).Assembly.GetType($"{typeof(MetadataV2Graph).Namespace}.{typeName}")
            .Should().NotBeNull();
    }

    [UnitTheory]
    [Operation(Operations.Metadata)]
    [InlineData("feature-dataset")]
    [InlineData("table")]
    [InlineData("raster-dataset")]
    [InlineData("tile-dataset")]
    [InlineData("process")]
    [InlineData("style")]
    [InlineData("document")]
    [InlineData("external-resource")]
    [InlineData("map")]
    [InlineData("dashboard")]
    [InlineData("form")]
    [InlineData("app")]
    [InlineData("workflow")]
    [InlineData("geoprocessing-service")]
    [InlineData("etl-pipeline")]
    [InlineData("future-kind")]
    public void Graph_RoundTrip_PreservesPersistedKindAndReferences(string kind)
    {
        var json = $$$"""
            {"resources":[{"metadata":{"id":"object-1"},"type":"{{{kind}}}","status":{},"futureProperty":{"enabled":true}}],
             "publications":[{"metadata":{"id":"layer-1"},"resourceId":"object-1","serviceId":"service-1","publicationType":"ogc-collection","status":{}}],
             "services":[{"metadata":{"id":"service-1"},"publicationIds":["layer-1"],"status":{}}]}
            """;
        var graph = JsonSerializer.Deserialize(json, MetadataV2JsonContext.Default.MetadataV2Graph)!;
        var roundTrip = JsonSerializer.Serialize(graph, MetadataV2JsonContext.Default.MetadataV2Graph);
        using var document = JsonDocument.Parse(roundTrip);
        var root = document.RootElement;

        root.GetProperty("resources")[0].GetProperty("type").GetString().Should().Be(kind);
        root.GetProperty("resources")[0].GetProperty("metadata").GetProperty("id").GetString().Should().Be("object-1");
        root.GetProperty("resources")[0].GetProperty("futureProperty").GetProperty("enabled").GetBoolean().Should().BeTrue();
        root.GetProperty("publications")[0].GetProperty("resourceId").GetString().Should().Be("object-1");
        root.GetProperty("publications")[0].GetProperty("publicationType").GetString().Should().Be("ogc-collection");
        root.GetProperty("services")[0].GetProperty("publicationIds")[0].GetString().Should().Be("layer-1");
    }
}
