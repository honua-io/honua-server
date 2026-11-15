// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

// A storage layer published through two WFS-enabled services (the compat snapshot's svc-*-ogc and
// svc-*-feature pair) is advertised once by the global WFS, in storage-layer order, while each
// publication typename and feature id stays resolvable.
[Collection("Database")]
[Protocol(TestProtocols.Wfs20)]
public sealed class Wfs20AdvertisedFeatureTypeTests
{
    private const string AdvertisedDuplicate = "pub-dup-feature-0";
    private const string HiddenDuplicate = "pub-dup-ogc-0";
    private const string ProjectedPublication = "pub-a-projected";
    private static readonly string[] CapabilitiesVersions = ["2.0.0", "1.1.0"];
    private static readonly AccessPolicy Anonymous = new() { AllowAnonymous = true };

    private static readonly MetadataV2Field[] Fields =
    [
        new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false },
        new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Nullable = true },
        new MetadataV2Field { Name = "shape", Type = MetadataV2FieldType.Geometry, Nullable = false }
    ];

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetCapabilities")]
    public async Task Wfs_GetCapabilities_StorageLayerInTwoWfsServices_IsAdvertisedOnceInStorageLayerOrder()
    {
        await WithFixtureAsync(BuildTwoLayerProvider(), async fixture =>
        {
            foreach (var version in CapabilitiesVersions)
            {
                var response = await fixture.Client.GetAsync(
                    $"/wfs?SERVICE=WFS&REQUEST=GetCapabilities&VERSION={version}");
                var content = await response.Content.ReadAsStringAsync();
                response.StatusCode.Should().Be(HttpStatusCode.OK, content);

                var names = XDocument.Parse(content)
                    .Descendants()
                    .Where(element => element.Name.LocalName == "FeatureType")
                    .Select(element => LocalPart(element.Elements().First(child => child.Name.LocalName == "Name").Value))
                    .ToArray();

                names.Should().Equal([AdvertisedDuplicate, ProjectedPublication], $"WFS {version} capabilities");
            }
        });
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "DescribeFeatureType")]
    public async Task Wfs_DescribeFeatureType_WithoutTypeNames_DescribesEachStorageLayerOnce()
    {
        await WithFixtureAsync(BuildTwoLayerProvider(), async fixture =>
        {
            var response = await fixture.Client.GetAsync(
                "/wfs?SERVICE=WFS&REQUEST=DescribeFeatureType&VERSION=2.0.0");
            var content = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, content);

            var elements = XDocument.Parse(content)
                .Root!
                .Elements()
                .Where(element => element.Name.LocalName == "element")
                .Select(element => element.Attribute("name")?.Value)
                .ToArray();

            elements.Should().Equal(AdvertisedDuplicate, ProjectedPublication);
        });
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    public async Task Wfs_GetFeature_UnadvertisedDuplicatePublication_RemainsResolvable()
    {
        await WithFixtureAsync(BuildTwoLayerProvider(), async fixture =>
        {
            var response = await fixture.Client.GetAsync(
                $"/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&TYPENAMES={HiddenDuplicate}&COUNT=1");
            var content = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, content);
            content.Should().Contain("FeatureCollection");
        });
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    public async Task Wfs_GetFeature_ResourceIdOfUnadvertisedDuplicateWithoutTypeNames_ReturnsFeature()
    {
        await WithFixtureAsync(BuildDuplicateOnlyProvider(), async fixture =>
        {
            var byTypeResponse = await fixture.Client.GetAsync(
                $"/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&TYPENAMES={HiddenDuplicate}&COUNT=1");
            var byTypeContent = await byTypeResponse.Content.ReadAsStringAsync();
            byTypeResponse.StatusCode.Should().Be(HttpStatusCode.OK, byTypeContent);
            var featureId = XDocument.Parse(byTypeContent)
                .Descendants()
                .Select(element => element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "id")?.Value)
                .First(id => id is not null && id.StartsWith(HiddenDuplicate + ".", StringComparison.Ordinal))!;

            var response = await fixture.Client.GetAsync(
                $"/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&RESOURCEID={Uri.EscapeDataString(featureId)}");
            var content = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, content);
            content.Should().Contain($"\"{featureId}\"");
        });
    }

    private static string LocalPart(string qualifiedName)
    {
        var separator = qualifiedName.IndexOf(':', StringComparison.Ordinal);
        return separator < 0 ? qualifiedName : qualifiedName[(separator + 1)..];
    }

    private static async Task WithFixtureAsync(TestMetadataV2GraphProvider provider, Func<WebAppFixture, Task> body)
    {
        var fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro)
            .ReplaceService<IMetadataV2GraphProvider>(provider);
        try
        {
            await fixture.InitializeAsync();
            await body(fixture);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    // Without TYPENAMES, GetFeature queries every advertised type; the projected layer has no backing
    // table in this fixture, so RESOURCEID cases use the duplicated layer alone.
    private static TestMetadataV2GraphProvider BuildDuplicateOnlyProvider()
        => new(AddDuplicatedLayer(new TestMetadataV2GraphBuilder()).Build());

    private static TestMetadataV2GraphProvider BuildTwoLayerProvider()
        => new(AddProjectedLayer(AddDuplicatedLayer(new TestMetadataV2GraphBuilder())).Build());

    private static TestMetadataV2GraphBuilder AddDuplicatedLayer(TestMetadataV2GraphBuilder builder)
        => builder
            .AddResource(
                "res-dup-layer",
                "Test Layer",
                MetadataV2ObjectType.FeatureDataset,
                accessPolicy: Anonymous,
                fields: Fields,
                spatial: new MetadataV2ObjectSpatial
                {
                    SpatialReference = MetadataV2SpatialReference.Wgs84,
                    GeometryType = MetadataV2GeometryType.Point
                })
            .AddStorageBinding("binding-dup-layer", "res-dup-layer", "features", storageLayerId: WebAppFixture.TestLayerId)
            .AddService("svc-dup-ogc", "dup_ogc", accessPolicy: Anonymous,
                protocols: [MetadataV2ServiceProtocols.OgcFeatures, MetadataV2ServiceProtocols.Wfs20])
            .AddService("svc-dup-feature", "dup_feature", accessPolicy: Anonymous,
                protocols: [MetadataV2ServiceProtocols.Wfs20])
            .AddPublication(HiddenDuplicate, "svc-dup-ogc", "res-dup-layer", layerIndex: 0,
                storageBindingId: "binding-dup-layer", serviceLocalId: "dup_layer",
                publicationType: MetadataV2LayerType.WfsFeatureType)
            .AddPublication(AdvertisedDuplicate, "svc-dup-feature", "res-dup-layer", layerIndex: 0,
                storageBindingId: "binding-dup-layer", serviceLocalId: "dup_layer",
                publicationType: MetadataV2LayerType.WfsFeatureType);

    private static TestMetadataV2GraphBuilder AddProjectedLayer(TestMetadataV2GraphBuilder builder)
        => builder
            .AddResource(
                "res-projected-layer",
                "Projected Layer",
                MetadataV2ObjectType.FeatureDataset,
                accessPolicy: Anonymous,
                fields: Fields,
                spatial: new MetadataV2ObjectSpatial
                {
                    SpatialReference = new MetadataV2SpatialReference
                    {
                        Srid = ProjectedExtentLayerCatalog.LayerSrid,
                        Crs = $"EPSG:{ProjectedExtentLayerCatalog.LayerSrid}",
                        IsGeographic = false
                    },
                    GeometryType = MetadataV2GeometryType.Point
                })
            .AddStorageBinding("binding-projected-layer", "res-projected-layer", "projected_metadata_layer",
                storageLayerId: ProjectedExtentLayerCatalog.LayerId)
            .AddService("svc-a-projected", "a_projected", accessPolicy: Anonymous,
                protocols: [MetadataV2ServiceProtocols.Wfs20])
            .AddPublication(ProjectedPublication, "svc-a-projected", "res-projected-layer", layerIndex: 0,
                storageBindingId: "binding-projected-layer", serviceLocalId: "projected_layer",
                publicationType: MetadataV2LayerType.WfsFeatureType);
}
