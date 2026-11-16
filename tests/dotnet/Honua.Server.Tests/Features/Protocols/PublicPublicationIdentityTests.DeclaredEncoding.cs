// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols;

public sealed partial class PublicPublicationIdentityTests
{
    private const string DeclaredNamespaceUri = "https://example.test/schemas/roads";
    private static readonly XNamespace LegacyGml = "http://www.opengis.net/gml";

    [IntegrationTheory]
    [InlineData("1.0.0")]
    [InlineData("1.1.0")]
    [Protocol(TestProtocols.Wfs10, TestProtocols.Wfs11)]
    [Operation(Operations.Query)]
    [InterfaceOperation(TestProtocols.Wfs10, "GetFeature")]
    [InterfaceOperation(TestProtocols.Wfs11, "GetFeature")]
    [Endpoint("GET /wfs")]
    public async Task WfsLegacyGetFeature_DeclaredEncoding_ShapesTheFeature(string version)
    {
        ConfigureDeclaredEncoding(new JsonObject
        {
            ["geometryAfterAttributes"] = true,
            ["hiddenFields"] = new JsonArray("name"),
            ["wfs10BoundedBy"] = new JsonObject { ["srsName"] = "EPSG:4326", ["coordinates"] = "0,0 1,1" }
        });
        BindPublicationsToSeededFeatures();

        var feature = await ReadFirstLegacyFeatureAsync(version, "ext:roads_primary");

        XNamespace declared = DeclaredNamespaceUri;
        feature.Name.Namespace.Should().Be(declared);
        feature.Elements(declared + "name").Should().BeEmpty("a hidden field is not written");
        feature.Elements().Last().Descendants().Should().Contain(
            element => element.Name.Namespace == LegacyGml, "the geometry follows the attributes");
        var box = feature.Elements(LegacyGml + "boundedBy").Elements(LegacyGml + "Box").ToArray();
        if (version == "1.0.0")
        {
            box.Should().ContainSingle().Which.Attribute("srsName")!.Value.Should().Be("EPSG:4326");
        }
        else
        {
            box.Should().BeEmpty("the declared bounding box applies to WFS 1.0 only");
        }
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wfs10)]
    [Operation(Operations.Query)]
    [InterfaceOperation(TestProtocols.Wfs10, "GetFeature")]
    [Endpoint("GET /wfs")]
    public async Task WfsLegacyGetFeature_FixtureNameWithoutMetadata_GetsNoSpecialTreatment()
    {
        // A conformance fixture layer name alone selects nothing: only metadata does.
        var graph = _fixture.GetCurrentV2GraphSnapshot().Graph;
        var resourceId = graph.Publications.Single(publication => publication.Metadata.Id == PublicationId).ResourceId;
        SetGraph(graph with
        {
            Revision = graph.Revision + 1,
            Resources = graph.Resources.Select(resource => resource.Metadata.Id == resourceId
                ? resource with { Metadata = resource.Metadata with { Name = "Other" } }
                : resource).ToArray(),
            Publications = graph.Publications.Select(publication => publication.Metadata.Id == PublicationId
                ? publication with { Metadata = publication.Metadata with { Id = "Other" } }
                : publication).ToArray(),
        });
        BindPublicationsToSeededFeatures();

        var feature = await ReadFirstLegacyFeatureAsync("1.0.0", "Other");

        feature.Name.Should().Be(XNamespace.Get("http://honua.io/wfs") + "Other");
        feature.Elements(LegacyGml + "boundedBy").Should().BeEmpty();
        feature.Elements().First().Descendants().Should().Contain(
            element => element.Name.Namespace == LegacyGml, "the geometry leads the attributes by default");
    }

    [IntegrationTheory]
    [InlineData("ListStoredQueries")]
    [InlineData("DescribeStoredQueries")]
    [Protocol(TestProtocols.Wfs20)]
    [Operation(Operations.GetMetadata)]
    [InterfaceOperation(TestProtocols.Wfs20, "ListStoredQueries")]
    [InterfaceOperation(TestProtocols.Wfs20, "DescribeStoredQueries")]
    [Endpoint("GET /wfs")]
    public async Task WfsStoredQueries_DeclaredNamespace_IsBoundOnTheResponse(string request)
    {
        ConfigureDeclaredEncoding(new JsonObject());

        var response = await ReadXmlAsync($"/wfs?SERVICE=WFS&VERSION=2.0.0&REQUEST={request}");

        response.Root!.GetNamespaceOfPrefix("ext").Should().Be(XNamespace.Get(DeclaredNamespaceUri));
    }

    private async Task<XElement> ReadFirstLegacyFeatureAsync(string version, string typeName)
    {
        var collection = await ReadXmlAsync(
            $"/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION={version}&TYPENAME={Uri.EscapeDataString(typeName)}&MAXFEATURES=1");
        return collection.Descendants(LegacyGml + "featureMember").First().Elements().Single();
    }

    private void ConfigureDeclaredEncoding(JsonObject legacyEncoding)
    {
        var declared = new JsonObject
        {
            ["namespace"] = new JsonObject { ["prefix"] = "ext", ["uri"] = DeclaredNamespaceUri },
            ["legacyEncoding"] = legacyEncoding
        };
        var graph = _fixture.GetCurrentV2GraphSnapshot().Graph;
        var resourceId = graph.Publications.Single(publication => publication.Metadata.Id == PublicationId).ResourceId;
        SetGraph(graph with
        {
            Revision = graph.Revision + 1,
            Resources = graph.Resources.Select(resource => resource.Metadata.Id == resourceId
                ? resource with { Extensions = new Dictionary<string, JsonElement> { ["wfs"] = JsonSerializer.SerializeToElement(declared) } }
                : resource).ToArray(),
            Publications = graph.Publications.Select(publication => publication.Metadata.Id == PublicationId
                ? publication with { Metadata = publication.Metadata with { Id = "roads_primary" } }
                : publication).ToArray(),
        });
    }
}
