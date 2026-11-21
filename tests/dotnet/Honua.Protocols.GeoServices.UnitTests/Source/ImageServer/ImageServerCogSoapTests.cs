// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Validation;
using Honua.Infrastructure.Validation;
using Honua.Protocols.GeoServices.ImageServer;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using static Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer.ImageServerCogStatisticsData;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

// SOAP GetServiceInfo and GetView describe a layer whose imagery is only a registered cloud COG from
// the COG grid, as the REST service document does; GetImage answers 501 because pixel blocks read
// raster_data only.
[Protocol(TestProtocols.ImageServer)]
public sealed class ImageServerCogSoapTests
{
    private const string GeoServicesNamespace = "http://www.esri.com/schemas/ArcGIS/10.8";

    private readonly IRasterStore _rasterStore = Substitute.For<IRasterStore>();
    private readonly ICogStore _cogStore = Substitute.For<ICogStore>();
    private readonly ICogTileResolver _tileResolver = Substitute.For<ICogTileResolver>();
    private readonly IImageServerLayerResolver _layerResolver = Substitute.For<IImageServerLayerResolver>();
    private readonly IMetadataV2GraphProvider _graphProvider = ImageServerTestFixtures.BuildGraphWithLayer(1);

    public ImageServerCogSoapTests()
    {
        _rasterStore.ListRastersAsync(default, default).ReturnsForAnyArgs(Array.Empty<RasterInfo>());
        _cogStore.ListByLayerAsync(1, Arg.Any<CancellationToken>()).Returns(new[] { Registration() });
        ServeOverview(_tileResolver);
        var resolution = new ImageServerLayerResolution(1, "publication-1", 1, null) { ServiceId = "service-1" };
        _layerResolver.ResolveFirstAccessibleLayerAsync(default!, default!, default, default, default).ReturnsForAnyArgs(resolution);
        _layerResolver.ValidatePublicationAsync(default!, default!, default, default, default!, default, default).ReturnsForAnyArgs(resolution);
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfo_CogOnlyLayer_ReportsTheCogGridAndOverviewStatistics()
    {
        var result = await PostSoapAsync("GetServiceInfo");

        result.Element("BandCount")!.Value.Should().Be("3");
        double.Parse(result.Element("PixelSizeX")!.Value, CultureInfo.InvariantCulture).Should().BeApproximately(38.2185, 1e-3);
        DoubleArray(result, "MinValues").Should().Equal(10, 50, 7);
        DoubleArray(result, "MaxValues").Should().Equal(30, 50, 7);
        result.Element("Histograms")!.Elements().Should().HaveCount(3);
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfo_CogOnlyLayerWithoutEntitlement_FaultsPaymentRequired()
    {
        var context = CreateSoapContext($"""<GetServiceInfo xmlns="{GeoServicesNamespace}" />""", WithoutEntitlement());

        var result = await InvokeAsync(context);

        await result.ExecuteAsync(context);
        context.Response.StatusCode.Should().Be(StatusCodes.Status402PaymentRequired);
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetView_CogOnlyLayer_DescribesTheCogExtent()
    {
        var result = await PostSoapAsync("GetView");

        var envelope = result.Element("Extent")!;
        double.Parse(envelope.Element("XMin")!.Value, CultureInfo.InvariantCulture).Should().BeApproximately(CogExtent.XMin, 1e-6);
        result.Element("BandCount")!.Value.Should().Be("3");
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task GetImage_CogOnlyLayer_AnswersNotImplemented()
    {
        var context = CreateSoapContext($"""
            <GetImage xmlns="{GeoServicesNamespace}">
              <ImageDescription>
                <Extent>
                  <XMin>{CogExtent.XMin}</XMin><YMin>{CogExtent.YMin}</YMin><XMax>{CogExtent.XMax}</XMax><YMax>{CogExtent.YMax}</YMax>
                  <SpatialReference><WKID>3857</WKID></SpatialReference>
                </Extent>
                <Width>8</Width>
                <Height>8</Height>
              </ImageDescription>
            </GetImage>
            """);

        var result = await InvokeAsync(context);

        await result.ExecuteAsync(context);
        context.Response.StatusCode.Should().Be(StatusCodes.Status501NotImplemented);
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public async Task ResolveFirstAccessibleLayer_CogOnlyLayer_SatisfiesThePrimaryRasterRequirement()
    {
        // The resolver serves publications with a storage binding; layer index 1 binds the COGs.
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource("resource-1", "test-layer", MetadataV2ObjectType.RasterDataset)
            .AddStorageBinding("binding-1", "resource-1", "test.rasters", storageLayerId: 1)
            .AddService("service-1", "image-svc-1", protocols: [ServiceProtocols.ImageServer])
            .AddPublication(
                "publication-1",
                "service-1",
                "resource-1",
                storageBindingId: "binding-1",
                layerIndex: 1,
                publicationType: MetadataV2LayerType.EsriImageLayer)
            .BuildProvider();
        var resolver = new MetadataV2ImageServerLayerResolver(new ResourceValidator(graph), graph, _rasterStore);
        var context = CreateSoapContext(string.Empty);

        var resolution = await resolver.ResolveFirstAccessibleLayerAsync(
            "image-svc-1", context, AuthorizationOperation.Metadata, CancellationToken.None, requirePrimaryRaster: true);
        _cogStore.ListByLayerAsync(1, Arg.Any<CancellationToken>()).Returns(Array.Empty<CogRegistration>());
        var withoutCogs = await resolver.ResolveFirstAccessibleLayerAsync(
            "image-svc-1", context, AuthorizationOperation.Metadata, CancellationToken.None, requirePrimaryRaster: true);

        resolution.ErrorResult.Should().BeNull();
        resolution.LayerId.Should().Be(1);
        withoutCogs.ErrorResult.Should().NotBeNull();
    }

    private async Task<XElement> PostSoapAsync(string operation)
    {
        var context = CreateSoapContext($"""<{operation} xmlns="{GeoServicesNamespace}" />""");
        var result = await InvokeAsync(context);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.Body.Position = 0;
        var document = await XDocument.LoadAsync(context.Response.Body, LoadOptions.None, CancellationToken.None);
        return document.Descendants().Single(element => element.Name.LocalName == "Result");
    }

    private Task<IResult> InvokeAsync(HttpContext context)
        => ImageServerSoapEndpoints.HandlePostSoapImageServer(
            "image-svc-1", context, _layerResolver, _rasterStore, exportHandler: null!, NullLogger<ImageServerSoapLog>.Instance);

    private static double[] DoubleArray(XElement result, string name)
        => result.Element(name)!.Elements().Select(value => double.Parse(value.Value, CultureInfo.InvariantCulture)).ToArray();

    private DefaultHttpContext CreateSoapContext(string operation, ILicenseEntitlementService? licensing = null)
    {
        var masks = Substitute.For<IFieldMaskSource>();
        masks.ResolveAsync(Arg.Any<MetadataV2Object>(), Arg.Any<CancellationToken>()).Returns(ImmutableArray<string>.Empty);
        var context = ImageServerTestFixtures.CreateContext("/services/image-svc-1/ImageServer", services =>
        {
            services.AddSingleton(masks);
            services.AddSingleton(_cogStore);
            services.AddSingleton(_tileResolver);
            services.AddSingleton(_graphProvider);
            services.AddSingleton(_layerResolver);
            services.AddValidationServices();
            if (licensing is not null)
            {
                services.AddSingleton(licensing);
            }
        });
        context.User = new ClaimsPrincipal(new ClaimsIdentity("test"));
        context.Request.ContentType = "text/xml";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes($"""
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body>{operation}</soap:Body>
            </soap:Envelope>
            """));
        return context;
    }
}
