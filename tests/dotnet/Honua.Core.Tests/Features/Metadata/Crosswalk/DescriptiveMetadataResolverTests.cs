// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Crosswalk;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Core.Tests.Features.Metadata.Crosswalk;

[Protocol(ProtocolNames.TestQuality)]
public sealed class DescriptiveMetadataResolverTests
{
    private static readonly MetadataV2ObjectMetadata Folder = new()
    {
        Id = "folder.parks",
        Name = "parks",
        Title = "Parks folder",
        License = "ODbL-1.0",
        Attribution = "Parks department",
        ContactPoint = new MetadataV2ContactPoint { Name = "Parks desk", Email = "parks@example.org" },
    };

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ForDataset_GovernanceUnsetOnDatasetAndService_FallsBackToFolder()
    {
        var graph = CrosswalkFixtures.Graph(folder: Folder);

        var record = DescriptiveMetadataResolver.ForDataset(graph, graph.Resources[0], graph.Services[0]);

        record.License.Should().Be("ODbL-1.0");
        record.Attribution.Should().Be("Parks department");
        record.Contacts.Should().ContainSingle().Which.Email.Should().Be("parks@example.org");
        record.AccessConstraints.Should().StartWith("License: ODbL-1.0");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ForDataset_ServiceSetsLicense_ServiceWinsOverFolder()
    {
        var graph = CrosswalkFixtures.Graph(
            service: new MetadataV2ObjectMetadata { Id = "service.parks", Name = "parks-service", License = "CC0-1.0" },
            folder: Folder);

        var record = DescriptiveMetadataResolver.ForDataset(graph, graph.Resources[0], graph.Services[0]);

        record.License.Should().Be("CC0-1.0");
        record.Attribution.Should().Be("Parks department");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ForDataset_LicenseLinkOnDataset_BlocksLicenseInheritance()
    {
        var graph = CrosswalkFixtures.Graph(
            resource: new MetadataV2ObjectMetadata
            {
                Id = "dataset.trails",
                Name = "trails",
                Links = [new MetadataV2Link { Rel = "license", Href = "https://example.org/terms" }],
            },
            folder: Folder);

        var record = DescriptiveMetadataResolver.ForDataset(graph, graph.Resources[0], graph.Services[0]);

        record.License.Should().BeNull();
        record.MetadataLink("license").Should().Be("https://example.org/terms");
        record.Attribution.Should().Be("Parks department");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ForDataset_PublisherOnServiceOnly_IsNotInherited()
    {
        var graph = CrosswalkFixtures.Graph(
            service: new MetadataV2ObjectMetadata { Id = "service.parks", Name = "parks-service", Publisher = "Hosting team" });

        DescriptiveMetadataResolver.ForDataset(graph, graph.Resources[0], graph.Services[0]).Publisher.Should().BeNull();
        DescriptiveMetadataResolver.ForService(graph, graph.Services[0]).Publisher.Should().Be("Hosting team");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ForDataset_Keywords_AreTrimmedAndDeduplicated()
    {
        var graph = CrosswalkFixtures.Graph(
            resource: new MetadataV2ObjectMetadata { Id = "dataset.trails", Name = "trails", Keywords = [" coastal ", "", "COASTAL", "survey"] });

        DescriptiveMetadataResolver.ForDataset(graph, graph.Resources[0], graph.Services[0]).Keywords
            .Should().Equal("coastal", "survey");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ForDataset_DescriptiveFields_AreNotInherited()
    {
        var graph = CrosswalkFixtures.Graph(folder: Folder with { Description = "Folder text", Keywords = ["folder"] });

        var record = DescriptiveMetadataResolver.ForDataset(graph, graph.Resources[0], graph.Services[0]);

        record.Title.Should().Be("trails");
        record.Description.Should().BeNull();
        record.Keywords.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ForDataset_LayerOverridesTitle_KeepsDatasetKeywords()
    {
        var graph = CrosswalkFixtures.Graph(resource: CrosswalkFixtures.FullDataset);
        var layer = new MetadataV2Publication { Metadata = new MetadataV2ObjectMetadata { Id = "layer.trails", Name = "trails", Title = "Trails (public)" } };

        var record = DescriptiveMetadataResolver.ForDataset(graph, graph.Resources[0], graph.Services[0], layer);

        record.Title.Should().Be("Trails (public)");
        record.Keywords.Should().Equal("trails", "recreation");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ForDataset_GeographicLayer_CarriesExtentAndSupportedCrs()
    {
        var graph = CrosswalkFixtures.Graph();

        var record = DescriptiveMetadataResolver.ForDataset(graph, graph.Resources[0], graph.Services[0]);

        record.Extent!.West.Should().Be(-123.5);
        record.Crs.Should().Equal("EPSG:4326", "EPSG:26910");
        record.TemporalExtent!.Start.Should().NotBeNull();
        record.MetadataDate.Should().Be(CrosswalkFixtures.Updated);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ForService_NoTitle_KeepsPublicNameAndInheritsGovernance()
    {
        var graph = CrosswalkFixtures.Graph(folder: Folder);

        var record = DescriptiveMetadataResolver.ForService(graph, graph.Services[0]);

        record.Kind.Should().Be(DescribedItemKind.Service);
        record.Title.Should().Be("parks-service");
        record.License.Should().Be("ODbL-1.0");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ForCapabilities_NoTitle_UsesFolderTitle()
    {
        var graph = CrosswalkFixtures.Graph(folder: Folder);

        DescriptiveMetadataResolver.ForCapabilities(graph, graph.Services[0]).Title.Should().Be("Parks folder");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ForService_NothingTitled_UsesPublicName()
    {
        var graph = CrosswalkFixtures.Graph();

        DescriptiveMetadataResolver.ForService(graph, graph.Services[0]).Title.Should().Be("parks-service");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ForCatalog_UsesEnvironmentMetadata()
    {
        var graph = CrosswalkFixtures.Graph(environment: new MetadataV2ObjectMetadata { Id = "graph", Name = "graph", Fees = "Free of charge" });

        var record = DescriptiveMetadataResolver.ForCatalog(graph);

        record.Title.Should().Be("county");
        record.Fees.Should().Be("Free of charge");
        record.AccessConstraints.Should().BeEmpty();
    }
}
