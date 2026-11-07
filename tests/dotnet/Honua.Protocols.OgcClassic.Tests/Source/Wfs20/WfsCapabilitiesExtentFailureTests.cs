// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Xml.Linq;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using NSubstitute;
using Xunit;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

[Collection("Database")]
[Protocol(TestProtocols.Wfs20)]
public sealed class WfsCapabilitiesExtentFailureTests
{
    private const int ProjectedSrid = 32618;

    [Theory]
    [Trait("Tier", "Integration")]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetCapabilities")]
    public async Task GetCapabilities_ExtentReadAvailability_AdvertisesOnlyHealthyLayers(bool extentReadFails)
    {
        var transform = Substitute.For<ICoordinateTransformService>();
        transform.TransformExtentAsync(
                Arg.Any<double>(), Arg.Any<double>(), Arg.Any<double>(), Arg.Any<double>(),
                ProjectedSrid, 4326, Arg.Any<CancellationToken>())
            .Returns(_ => extentReadFails
                ? ValueTask.FromException<(double, double, double, double)?>(new InvalidOperationException("Extent unavailable"))
                : ValueTask.FromResult<(double, double, double, double)?>((-5d, 40d, -4d, 41d)));
        await using var fixture = new WebAppFixture()
            .WithTestLicense(HonuaEdition.Pro)
            .ReplaceService<IMetadataV2GraphProvider>(PublishedLayers())
            .ReplaceService<ICoordinateTransformService>(transform);
        await fixture.InitializeAsync();

        using var response = await fixture.Client.GetAsync("/wfs?SERVICE=WFS&REQUEST=GetCapabilities&VERSION=2.0.0");
        var content = await response.Content.ReadAsStringAsync();

        Assert.Single(transform.ReceivedCalls().Where(call =>
            call.GetMethodInfo().Name == nameof(ICoordinateTransformService.TransformExtentAsync)));
        Assert.True(response.StatusCode == HttpStatusCode.OK, content);
        var titles = XDocument.Parse(content).Descendants()
            .Where(element => element.Name.LocalName == "FeatureType")
            .Select(element => element.Elements().Single(child => child.Name.LocalName == "Title").Value)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] expectedTitles = extentReadFails ? ["healthy_extent"] : ["healthy_extent", "projected_extent"];
        Assert.Equal(expectedTitles, titles);
    }

    private static TestMetadataV2GraphProvider PublishedLayers()
    {
        const string ServiceId = "svc-capabilities-extents";
        var builder = new TestMetadataV2GraphBuilder().AddService(
            ServiceId, "capabilities_extents", protocols: [MetadataV2ServiceProtocols.Wfs20],
            accessPolicy: new AccessPolicy { AllowAnonymous = true });
        foreach (var (name, layerId, srid) in new[] { ("healthy_extent", 1, 4326), ("projected_extent", 2, ProjectedSrid) })
        {
            var resourceId = "res-" + name;
            var bindingId = "binding-" + name;
            builder.AddResource(resourceId, name,
                    fields:
                    [
                        new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false },
                        new MetadataV2Field { Name = "shape", Type = MetadataV2FieldType.Geometry, Nullable = false }
                    ],
                    accessPolicy: new AccessPolicy { AllowAnonymous = true },
                    spatial: new MetadataV2ResourceSpatial
                    {
                        SpatialReference = new MetadataV2SpatialReference { Srid = srid, Crs = $"EPSG:{srid}", IsGeographic = srid == 4326 },
                        GeometryType = MetadataV2GeometryType.Point,
                        Bbox = srid == 4326
                            ? new MetadataV2Bbox { West = -5, South = 40, East = -4, North = 41 }
                            : new MetadataV2Bbox { West = 500_000, South = 4_100_000, East = 600_000, North = 4_200_000 }
                    })
                .AddStorageBinding(bindingId, resourceId, name, storageLayerId: layerId)
                .AddPublication("pub-" + name, ServiceId, resourceId,
                    layerIndex: layerId, storageBindingId: bindingId,
                    publicationType: MetadataV2PublicationType.WfsFeatureType);
        }
        return new TestMetadataV2GraphProvider(builder.Build());
    }
}
