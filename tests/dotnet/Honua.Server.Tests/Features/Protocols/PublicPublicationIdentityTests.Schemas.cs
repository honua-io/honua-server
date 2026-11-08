// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Xml;
using System.Xml.Linq;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols;

public sealed partial class PublicPublicationIdentityTests
{
    [IntegrationTheory]
    [InlineData("1.0.0", "Other", "Other")]
    [InlineData("1.0.0", "Points", "Points")]
    [InlineData("1.0.0", "normal-resource", "authored:layer")]
    [InlineData("1.1.0", "Other", "Other")]
    [InlineData("1.1.0", "Points", "Points")]
    [InlineData("1.1.0", "normal-resource", "authored:layer")]
    [InlineData("2.0.0", "Other", "Other")]
    [InlineData("2.0.0", "Points", "Points")]
    [InlineData("2.0.0", "normal-resource", "authored:layer")]
    [Protocol(TestProtocols.Wfs10, TestProtocols.Wfs11, TestProtocols.Wfs20)]
    [Operation(Operations.GetMetadata)]
    [InterfaceOperation(TestProtocols.Wfs10, "DescribeFeatureType")]
    [InterfaceOperation(TestProtocols.Wfs11, "DescribeFeatureType")]
    [InterfaceOperation(TestProtocols.Wfs20, "DescribeFeatureType")]
    [Endpoint("GET /wfs")]
    public async Task DescribeFeatureType_AdvertisedNamespace_DefinesPublicationInThatNamespace(
        string version, string resourceName, string publicationId)
    {
        ConfigureSchemaPublication(resourceName, publicationId);
        var capabilities = await ReadXmlAsync($"/wfs?SERVICE=WFS&REQUEST=GetCapabilities&VERSION={version}");
        var localName = XmlConvert.EncodeLocalName(publicationId);
        var advertisedName = capabilities.Descendants().Single(name => name.Name.LocalName == "Name"
            && name.Parent!.Name.LocalName == "FeatureType"
            && name.Value.EndsWith($":{localName}", StringComparison.Ordinal));
        var prefix = advertisedName.Value.Split(':', 2)[0];
        var advertisedNamespace = advertisedName.GetNamespaceOfPrefix(prefix)!;
        var typeParameter = version == "2.0.0" ? "TYPENAMES" : "TYPENAME";
        var schema = await ReadXmlAsync($"/wfs?SERVICE=WFS&REQUEST=DescribeFeatureType&VERSION={version}&{typeParameter}={Uri.EscapeDataString(advertisedName.Value)}");

        schema.Root!.Attribute("targetNamespace")!.Value.Should().Be(advertisedNamespace.NamespaceName);
        XNamespace xsd = "http://www.w3.org/2001/XMLSchema";
        schema.Root.Elements(xsd + "element").Should().Contain(element => element.Attribute("name")!.Value == localName);
    }

    [IntegrationTheory]
    [InlineData("1.1.0")]
    [InlineData("2.0.0")]
    [Protocol(TestProtocols.Wfs11, TestProtocols.Wfs20)]
    [Operation(Operations.GetMetadata)]
    [InterfaceOperation(TestProtocols.Wfs11, "DescribeFeatureType")]
    [InterfaceOperation(TestProtocols.Wfs20, "DescribeFeatureType")]
    [Endpoint("GET /wfs")]
    public async Task DescribeFeatureType_MixedNamespaces_ImportsResolvablePublicationSchema(string version)
    {
        ConfigureDistinctStyleResources();
        ConfigureSchemaPublication("Other", "Other");
        var schema = await ReadXmlAsync($"/wfs?SERVICE=WFS&REQUEST=DescribeFeatureType&VERSION={version}");
        XNamespace xsd = "http://www.w3.org/2001/XMLSchema";
        const string dataNamespace = "http://www.opengis.net/cite/data";
        var import = schema.Root!.Elements(xsd + "import")
            .SingleOrDefault(element => element.Attribute("namespace")?.Value == dataNamespace);
        import.Should().NotBeNull();
        var location = new Uri(import!.Attribute("schemaLocation")!.Value);
        var imported = await ReadXmlAsync(location.PathAndQuery);

        imported.Root!.Attribute("targetNamespace")!.Value.Should().Be(dataNamespace);
        imported.Root.Elements(xsd + "element").Should().Contain(element => element.Attribute("name")!.Value == "Other");
    }

    private void ConfigureSchemaPublication(string resourceName, string publicationId)
    {
        var graph = _fixture.GetCurrentV2GraphSnapshot().Graph;
        var primaryResourceId = graph.Publications.Single(publication => publication.Metadata.Id == PublicationId).ResourceId;
        SetGraph(graph with
        {
            Revision = graph.Revision + 1,
            Resources = graph.Resources.Select(resource => resource.Metadata.Id == primaryResourceId
                ? resource with { Metadata = resource.Metadata with { Name = resourceName } } : resource).ToArray(),
            Publications = graph.Publications.Select(publication => publication.Metadata.Id == PublicationId
                ? publication with { Metadata = publication.Metadata with { Id = publicationId, Name = publicationId } } : publication).ToArray(),
        });
    }
}
