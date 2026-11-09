// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Xml;
using System.Xml.Linq;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols;

public sealed partial class PublicPublicationIdentityTests
{
    [IntegrationTheory]
    [InlineData("Other", "Other")]
    [InlineData("Points", "Points")]
    [InlineData("normal-resource", "authored:layer")]
    [Protocol(TestProtocols.Wfs20)]
    [Operation(Operations.Query)]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    [Endpoint("GET /wfs")]
    public async Task WfsGetFeature_AdvertisedNamespace_QualifiesFeatureAndProperties(string resourceName, string publicationId)
    {
        ConfigureSchemaPublication(resourceName, publicationId);
        BindPublicationsToSeededFeatures();
        var localName = XmlConvert.EncodeLocalName(publicationId);
        var schema = await ReadXmlAsync($"/wfs?SERVICE=WFS&REQUEST=DescribeFeatureType&VERSION=2.0.0&TYPENAMES={localName}");
        XNamespace featureNamespace = schema.Root!.Attribute("targetNamespace")!.Value;

        var collection = await ReadXmlAsync($"/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&TYPENAMES={localName}&COUNT=1");

        XNamespace wfs = "http://www.opengis.net/wfs/2.0";
        XNamespace gml = "http://www.opengis.net/gml/3.2";
        var feature = collection.Root!.Elements(wfs + "member").SelectMany(member => member.Elements()).Should().ContainSingle().Subject;
        feature.Name.Should().Be(featureNamespace + localName);
        var properties = feature.Elements().Where(property => property.Name.Namespace != gml).ToArray();
        properties.Should().NotBeEmpty();
        properties.Should().OnlyContain(property => property.Name.Namespace == featureNamespace);
    }

    [IntegrationTheory]
    [InlineData("Other", "Other")]
    [InlineData("Points", "Points")]
    [InlineData("normal-resource", "authored:layer")]
    [Protocol(TestProtocols.Wfs20)]
    [Operation(Operations.Query)]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    [Endpoint("GET /wfs")]
    public async Task WfsGetFeature_SchemaLocation_ResolvesDescribedFeatureNamespace(string resourceName, string publicationId)
    {
        ConfigureSchemaPublication(resourceName, publicationId);
        var localName = XmlConvert.EncodeLocalName(publicationId);
        var schema = await ReadXmlAsync($"/wfs?SERVICE=WFS&REQUEST=DescribeFeatureType&VERSION=2.0.0&TYPENAMES={localName}");
        var featureNamespace = schema.Root!.Attribute("targetNamespace")!.Value;

        var collection = await ReadXmlAsync($"/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&TYPENAMES={localName}&COUNT=0");

        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
        var locations = collection.Root!.Attribute(xsi + "schemaLocation")!.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var namespaceIndex = Array.IndexOf(locations, featureNamespace);
        namespaceIndex.Should().BeGreaterThanOrEqualTo(0);
        var location = new Uri(locations[namespaceIndex + 1]);
        var locatedSchema = await ReadXmlAsync(location.PathAndQuery);
        locatedSchema.Root!.Attribute("targetNamespace")!.Value.Should().Be(featureNamespace);
    }

    private void BindPublicationsToSeededFeatures()
    {
        var graph = _fixture.GetCurrentV2GraphSnapshot().Graph;
        SetGraph(graph with
        {
            Revision = graph.Revision + 1,
            StorageBindings = graph.StorageBindings.Select(binding => binding with { StorageLayerId = WebAppFixture.TestLayerId }).ToArray(),
        });
    }
}
