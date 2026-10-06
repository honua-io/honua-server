// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.Multidimensional.Abstractions;
using Honua.Core.Features.Raster.Multidimensional.Domain;
using Honua.Core.Features.Raster.Services;
using Honua.Protocols.GeoServices.ImageServer;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

/// <summary>
/// SOAP responses for one ImageServer fixture must carry the same catalog, multidimensional,
/// key-property, histogram, and tile-cache facts the REST binding returns.
/// </summary>
[Collection("Database.GeoServicesRaster")]
[Protocol(TestProtocols.ImageServer)]
public sealed class ImageServerSoapParityTests : IAsyncLifetime
{
    private const string ArcGisNamespace = "http://www.esri.com/schemas/ArcGIS/10.8";
    private const string XsiNamespace = "http://www.w3.org/2001/XMLSchema-instance";
    private const string NameFilter = "( Name = 'Test Raster RGB' )";
    private const double ServiceLowCellSize = 0.0021875;
    private const double ServiceHighCellSize = 0.00234375;
    private static readonly long[] HistogramCountsBand1 = [200, 3776, 120];
    private static readonly long[] HistogramCountsBand2 = [120, 3916, 60];
    private static readonly long[] HistogramCountsBand3 = [60, 3976, 60];

    private WebAppFixture _fixture = null!;

    public async Task InitializeAsync()
    {
        _fixture = await CreateFixtureAsync(enableTileCache: false);
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [InterfaceOperation(TestProtocols.ImageServer, "GetCatalogItemCount")]
    [InterfaceOperation(TestProtocols.ImageServer, "GetCatalogItemIDs")]
    [InterfaceOperation(TestProtocols.ImageServer, "GetCatalogItems")]
    [Endpoint("POST /services/{serviceId}/ImageServer")]
    public async Task CatalogSoapOperations_MatchRestQuery()
    {
        var service = ServicePath();
        using var restCount = await _fixture.Client.GetAsync($"{service}/query?where=1%3D1&returnCountOnly=true&f=json");
        using var restIds = await _fixture.Client.GetAsync($"{service}/query?where=1%3D1&returnIdsOnly=true&f=json");
        using var restNamedCount = await _fixture.Client.GetAsync(
            $"{service}/query?where={Uri.EscapeDataString(NameFilter)}&returnCountOnly=true&f=json");
        using var restNamedIds = await _fixture.Client.GetAsync(
            $"{service}/query?where={Uri.EscapeDataString(NameFilter)}&returnIdsOnly=true&f=json");
        using var restItems = await _fixture.Client.GetAsync(
            $"{service}/query?where={Uri.EscapeDataString(NameFilter)}&outFields=OBJECTID,Name&returnGeometry=true&outSR=4326&f=json");

        var countJson = await ReadJsonAsync(restCount);
        var idsJson = await ReadJsonAsync(restIds);
        var namedCountJson = await ReadJsonAsync(restNamedCount);
        var namedIdsJson = await ReadJsonAsync(restNamedIds);
        var itemsJson = await ReadJsonAsync(restItems);

        var count = await PostSoapAsync(CatalogRequest("GetCatalogItemCount", where: null));
        count.Status.Should().Be(HttpStatusCode.OK, count.Body);
        count.Body.Should().NotContain("Unsupported ImageServer operation");
        IntValue(count.Document, "Result").Should().Be(countJson.GetProperty("count").GetInt32());
        IntValue(count.Document, "Result").Should().Be(2);

        var namedCount = await PostSoapAsync(CatalogRequest("GetCatalogItemCount", NameFilter));
        namedCount.Status.Should().Be(HttpStatusCode.OK, namedCount.Body);
        IntValue(namedCount.Document, "Result").Should().Be(namedCountJson.GetProperty("count").GetInt32());
        IntValue(namedCount.Document, "Result").Should().Be(1);

        var ids = await PostSoapAsync(CatalogRequest("GetCatalogItemIDs", where: null));
        ids.Status.Should().Be(HttpStatusCode.OK, ids.Body);
        FidSet(ids.Document).Should().Equal(ObjectIds(idsJson));
        FidSet(ids.Document).Should().Equal(1, 3);

        var namedIds = await PostSoapAsync(CatalogRequest("GetCatalogItemIDs", NameFilter));
        namedIds.Status.Should().Be(HttpStatusCode.OK, namedIds.Body);
        FidSet(namedIds.Document).Should().Equal(ObjectIds(namedIdsJson));
        FidSet(namedIds.Document).Should().Equal(1);

        var items = await PostSoapAsync(CatalogItemsRequest());
        items.Status.Should().Be(HttpStatusCode.OK, items.Body);
        var record = items.Document.Descendants().Single(element => element.Name.LocalName == "Record");
        var values = record.Descendants().Where(element => element.Name.LocalName == "Value").ToArray();
        values.Should().HaveCountGreaterThanOrEqualTo(3);
        values[0].Value.Should().Be("1");
        values[1].Value.Should().Be("Test Raster RGB");
        var polygon = values.Single(element => element.Attribute(XName.Get("type", XsiNamespace))?.Value.Contains("PolygonN", StringComparison.Ordinal) == true);
        var soapPoints = polygon.Descendants()
            .Where(element => element.Name.LocalName == "Point")
            .Select(point => (
                X: double.Parse(point.Elements().Single(element => element.Name.LocalName == "X").Value, CultureInfo.InvariantCulture),
                Y: double.Parse(point.Elements().Single(element => element.Name.LocalName == "Y").Value, CultureInfo.InvariantCulture)))
            .ToArray();
        var ring = itemsJson.GetProperty("features")[0].GetProperty("geometry").GetProperty("rings")[0];
        soapPoints.Should().HaveCount(ring.GetArrayLength());
        for (var index = 0; index < soapPoints.Length; index++)
        {
            soapPoints[index].X.Should().BeApproximately(ring[index][0].GetDouble(), 1e-9);
            soapPoints[index].Y.Should().BeApproximately(ring[index][1].GetDouble(), 1e-9);
        }

        items.Document.Descendants().Single(element => element.Name.LocalName == "GeometryDef")
            .Descendants().Single(element => element.Name.LocalName == "WKID")
            .Value.Should().Be(itemsJson.GetProperty("features")[0]
                .GetProperty("geometry").GetProperty("spatialReference").GetProperty("wkid").GetInt32().ToString(CultureInfo.InvariantCulture));

        var rejected = await PostSoapAsync(CatalogRequest("GetCatalogItemCount", where: null, catalogName: "Mosaic"));
        rejected.Status.Should().Be(HttpStatusCode.BadRequest);

        var unknownField = await PostSoapAsync(
            $"""
            <GetCatalogItems xmlns="{ArcGisNamespace}">
              <QueryFilter><SubFields>NotAField</SubFields></QueryFilter>
            </GetCatalogItems>
            """);
        unknownField.Status.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [InterfaceOperation(TestProtocols.ImageServer, "GetRasterKeyProperties")]
    [Endpoint("POST /services/{serviceId}/ImageServer")]
    public async Task KeyPropertiesSoap_MatchesRestKeyProperties()
    {
        var service = ServicePath();
        using var rest = await _fixture.Client.GetAsync($"{service}/keyProperties?f=json");
        using var restItem = await _fixture.Client.GetAsync($"{service}/3/info/keyProperties?f=json");
        var serviceJson = await ReadJsonAsync(rest);
        var itemJson = await ReadJsonAsync(restItem);

        var properties = await PostSoapAsync($"""<GetKeyProperties xmlns="{ArcGisNamespace}" />""");
        properties.Status.Should().Be(HttpStatusCode.OK, properties.Body);
        AssertKeyProperties(properties.Document, serviceJson);
        Property(properties.Document, "LowCellSize").Should().Be(ServiceLowCellSize);
        Property(properties.Document, "HighCellSize").Should().Be(ServiceHighCellSize);
        PropertyText(properties.Document, "BandDefinitionKeyword").Should().BeEmpty();
        PropertyText(properties.Document, "BandDefinitionKeyword").Should().NotBe("NONE");

        var extended = await PostSoapAsync(
            $"""
            <GetKeyPropertiesX xmlns="{ArcGisNamespace}" xmlns:xsi="{XsiNamespace}">
              <RenderingRule xsi:nil="true" />
            </GetKeyPropertiesX>
            """);
        extended.Status.Should().Be(HttpStatusCode.OK, extended.Body);
        AssertKeyProperties(extended.Document, serviceJson);

        var item = await PostSoapAsync(
            $"""
            <GetRasterKeyProperties xmlns="{ArcGisNamespace}">
              <RID>3</RID>
            </GetRasterKeyProperties>
            """);
        item.Status.Should().Be(HttpStatusCode.OK, item.Body);
        item.Body.Should().NotContain("Unsupported ImageServer operation");
        AssertKeyProperties(item.Document, itemJson);
        Property(item.Document, "LowCellSize").Should().Be(0.01);
        Property(item.Document, "HighCellSize").Should().Be(0.02);
    }

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [InterfaceOperation(TestProtocols.ImageServer, "GetMultidimensionalInfo")]
    [Endpoint("POST /services/{serviceId}/ImageServer")]
    public async Task MultidimensionalInfoAndSupportsTime_MatchRest()
    {
        var service = ServicePath();
        using var restInfo = await _fixture.Client.GetAsync($"{service}?f=json");
        using var restMultidim = await _fixture.Client.GetAsync($"{service}/multidimensionalInfo?f=json");
        var info = await ReadJsonAsync(restInfo);
        var multidim = await ReadJsonAsync(restMultidim);

        info.GetProperty("hasMultidimensions").GetBoolean().Should().BeTrue();
        var restVariable = multidim.GetProperty("multidimensionalInfo").GetProperty("variables")[0];
        restVariable.GetProperty("name").GetString().Should().Be("sea_surface_temperature");

        var soapInfo = await PostSoapAsync($"""<GetServiceInfo xmlns="{ArcGisNamespace}" />""");
        soapInfo.Status.Should().Be(HttpStatusCode.OK, soapInfo.Body);
        Element(soapInfo.Document, "SupportsTime").Value.Should().Be("true");
        Element(soapInfo.Document, "StartTimeFieldName").Value.Should().Be(
            info.GetProperty("timeInfo").GetProperty("startTimeField").GetString());
        var extent = Element(soapInfo.Document, "TimeExtent");
        UnixMilliseconds(extent, "Start").Should().Be(info.GetProperty("timeInfo").GetProperty("timeExtent")[0].GetInt64());
        UnixMilliseconds(extent, "End").Should().Be(info.GetProperty("timeInfo").GetProperty("timeExtent")[1].GetInt64());

        var soapMultidim = await PostSoapAsync($"""<GetMultidimensionalInfo xmlns="{ArcGisNamespace}" />""");
        soapMultidim.Status.Should().Be(HttpStatusCode.OK, soapMultidim.Body);
        var result = soapMultidim.Document.Descendants().Single(element => element.Name.LocalName == "Result");
        result.Attribute(XName.Get("nil", XsiNamespace)).Should().BeNull();
        var variable = result.Descendants().Single(element => element.Name.LocalName == "Name" && element.Value == "sea_surface_temperature").Parent!;
        Child(variable, "Unit").Value.Should().Be(restVariable.GetProperty("unit").GetString());
        Child(variable, "Description").Value.Should().Be(restVariable.GetProperty("description").GetString());
        foreach (var restDimension in restVariable.GetProperty("dimensions").EnumerateArray())
        {
            var name = restDimension.GetProperty("name").GetString()!;
            var soapDimension = variable.Descendants().Single(element =>
                element.Name.LocalName == "Name" && element.Value == name && element.Parent != variable).Parent!;
            Child(soapDimension, "DimensionSize").Value.Should().Be(
                restDimension.GetProperty("dimensionSize").GetInt64().ToString(CultureInfo.InvariantCulture));
            if (restDimension.TryGetProperty("values", out var values))
            {
                var soapValues = Child(soapDimension, "Values").Elements()
                    .Select(element => double.Parse(element.Value, CultureInfo.InvariantCulture))
                    .ToArray();
                soapValues.Should().Equal(values.EnumerateArray().Select(value => value.GetDouble()));
            }
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [InterfaceOperation(TestProtocols.ImageServer, "ComputeHistograms")]
    [Endpoint("POST /services/{serviceId}/ImageServer")]
    public async Task HistogramsSoap_MatchRestHistograms()
    {
        using var rest = await _fixture.Client.GetAsync($"{ServicePath()}/histograms?f=json");
        var json = await ReadJsonAsync(rest);
        var expected = json.GetProperty("histograms").EnumerateArray().ToArray();
        expected.Should().HaveCount(3);
        expected[0].GetProperty("counts")[0].GetInt64().Should().Be(200);
        expected.Sum(histogram => histogram.GetProperty("counts").EnumerateArray().Sum(count => count.GetInt64()))
            .Should().Be(4096 * 3);

        var computed = await PostSoapAsync(
            $"""
            <ComputeHistograms xmlns="{ArcGisNamespace}" xmlns:xsi="{XsiNamespace}">
              <Geometry xsi:nil="true" />
              <MosaicRule xsi:nil="true" />
              <PixelSize xsi:nil="true" />
              <RenderingRule xsi:nil="true" />
            </ComputeHistograms>
            """);
        computed.Status.Should().Be(HttpStatusCode.OK, computed.Body);
        computed.Body.Should().NotContain("Unsupported ImageServer operation");
        AssertHistograms(computed.Document.Descendants().Single(element => element.Name.LocalName == "Result"), expected);

        var serviceInfo = await PostSoapAsync($"""<GetServiceInfo xmlns="{ArcGisNamespace}" />""");
        serviceInfo.Status.Should().Be(HttpStatusCode.OK, serviceInfo.Body);
        AssertHistograms(Element(serviceInfo.Document, "Histograms"), expected);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [InterfaceOperation(TestProtocols.ImageServer, "GetCacheDescriptionInfo")]
    [InterfaceOperation(TestProtocols.ImageServer, "GetTileCacheInfo")]
    [InterfaceOperation(TestProtocols.ImageServer, "GetTileImageInfo")]
    [Endpoint("POST /services/{serviceId}/ImageServer")]
    public async Task TileCacheSoap_StaysUncachedWhenMetadataIsDisabled()
    {
        using var rest = await _fixture.Client.GetAsync($"{ServicePath()}?f=json");
        var info = await ReadJsonAsync(rest);
        info.GetProperty("singleFusedMapCache").GetBoolean().Should().BeFalse();

        var fixedScale = await PostSoapAsync($"""<IsFixedScaleImage xmlns="{ArcGisNamespace}" />""");
        fixedScale.Status.Should().Be(HttpStatusCode.OK, fixedScale.Body);
        Element(fixedScale.Document, "Result").Value.Should().Be("false");

        var description = await PostSoapAsync($"""<GetCacheDescriptionInfo xmlns="{ArcGisNamespace}" />""");
        description.Status.Should().Be(HttpStatusCode.OK, description.Body);
        description.Body.Should().NotContain("Unsupported ImageServer operation");
        Element(description.Document, "TileCacheInfo")
            .Attribute(XName.Get("nil", XsiNamespace))!.Value.Should().Be("true");

        var tileInfo = await PostSoapAsync($"""<GetTileCacheInfo xmlns="{ArcGisNamespace}" />""");
        tileInfo.Status.Should().Be(HttpStatusCode.BadRequest, tileInfo.Body);
        tileInfo.Body.Should().NotContain("Unsupported ImageServer operation");

        var imageTile = await PostSoapAsync(ImageTileRequest());
        imageTile.Status.Should().Be(HttpStatusCode.BadRequest, imageTile.Body);
        imageTile.Body.Should().NotContain("Unsupported ImageServer operation");
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [InterfaceOperation(TestProtocols.ImageServer, "GetImageTile")]
    [Endpoint("POST /services/{serviceId}/ImageServer")]
    public async Task TileCacheSoap_MatchesRestWhenMetadataIsEnabled()
    {
        var fixture = await CreateFixtureAsync(enableTileCache: true);
        try
        {
            using var restInfo = await fixture.Client.GetAsync($"{ServicePath()}?f=json");
            using var restTile = await fixture.Client.GetAsync($"{ServicePath()}/tile/0/0/0");
            var info = await ReadJsonAsync(restInfo);
            var tileBytes = await restTile.Content.ReadAsByteArrayAsync();
            restTile.StatusCode.Should().Be(HttpStatusCode.OK);
            info.GetProperty("singleFusedMapCache").GetBoolean().Should().BeTrue();
            var tileInfo = info.GetProperty("tileInfo");

            var fixedScale = await PostSoapAsync(fixture, $"""<IsFixedScaleImage xmlns="{ArcGisNamespace}" />""");
            fixedScale.Status.Should().Be(HttpStatusCode.OK, fixedScale.Body);
            Element(fixedScale.Document, "Result").Value.Should().Be("true");

            var description = await PostSoapAsync(fixture, $"""<GetCacheDescriptionInfo xmlns="{ArcGisNamespace}" />""");
            description.Status.Should().Be(HttpStatusCode.OK, description.Body);
            description.Body.Should().NotContain("Unsupported ImageServer operation");
            AssertTileCache(Element(description.Document, "TileCacheInfo"), tileInfo);
            Element(description.Document, "ServiceType").Value.Should().Be("esriCachedMapServiceSingleFused");
            var imageInfo = Element(description.Document, "TileImageInfo");
            Child(imageInfo, "CacheTileFormat").Value.Should().Be(tileInfo.GetProperty("format").GetString());
            Child(imageInfo, "CompressionQuality").Value.Should().Be("0");
            tileInfo.GetProperty("compressionQuality").ValueKind.Should().Be(JsonValueKind.Null);

            var cacheInfo = await PostSoapAsync(fixture, $"""<GetTileCacheInfo xmlns="{ArcGisNamespace}" />""");
            cacheInfo.Status.Should().Be(HttpStatusCode.OK, cacheInfo.Body);
            AssertTileCache(Element(cacheInfo.Document, "Result"), tileInfo);

            var tileImage = await PostSoapAsync(fixture, $"""<GetTileImageInfo xmlns="{ArcGisNamespace}" />""");
            tileImage.Status.Should().Be(HttpStatusCode.OK, tileImage.Body);
            Child(Element(tileImage.Document, "Result"), "CacheTileFormat").Value.Should().Be("PNG");

            var imageTile = await PostSoapAsync(fixture, ImageTileRequest());
            imageTile.Status.Should().Be(HttpStatusCode.OK, imageTile.Body);
            Convert.FromBase64String(Element(imageTile.Document, "Result").Value).Should().Equal(tileBytes);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static void AssertTileCache(XElement cache, JsonElement tileInfo)
    {
        Child(cache, "TileCols").Value.Should().Be(tileInfo.GetProperty("cols").GetInt32().ToString(CultureInfo.InvariantCulture));
        Child(cache, "TileRows").Value.Should().Be(tileInfo.GetProperty("rows").GetInt32().ToString(CultureInfo.InvariantCulture));
        Child(cache, "DPI").Value.Should().Be(tileInfo.GetProperty("dpi").GetInt32().ToString(CultureInfo.InvariantCulture));
        var origin = Child(cache, "TileOrigin");
        double.Parse(Child(origin, "X").Value, CultureInfo.InvariantCulture)
            .Should().BeApproximately(tileInfo.GetProperty("origin").GetProperty("x").GetDouble(), 1e-6);
        double.Parse(Child(origin, "Y").Value, CultureInfo.InvariantCulture)
            .Should().BeApproximately(tileInfo.GetProperty("origin").GetProperty("y").GetDouble(), 1e-6);
        Child(cache, "SpatialReference").Descendants().Single(element => element.Name.LocalName == "WKID")
            .Value.Should().Be(tileInfo.GetProperty("spatialReference").GetProperty("wkid").GetInt32().ToString(CultureInfo.InvariantCulture));
        Child(cache, "SpatialReference").Descendants().Single(element => element.Name.LocalName == "LatestWKID")
            .Value.Should().Be(tileInfo.GetProperty("spatialReference").GetProperty("latestWkid").GetInt32().ToString(CultureInfo.InvariantCulture));

        var soapLods = cache.Descendants().Where(element => element.Name.LocalName == "LODInfo").ToArray();
        var restLods = tileInfo.GetProperty("lods").EnumerateArray().ToArray();
        soapLods.Should().HaveCount(restLods.Length);
        for (var index = 0; index < restLods.Length; index++)
        {
            Child(soapLods[index], "LevelID").Value.Should().Be(
                restLods[index].GetProperty("level").GetInt32().ToString(CultureInfo.InvariantCulture));
            double.Parse(Child(soapLods[index], "Resolution").Value, CultureInfo.InvariantCulture)
                .Should().BeApproximately(restLods[index].GetProperty("resolution").GetDouble(), 1e-6);
            double.Parse(Child(soapLods[index], "Scale").Value, CultureInfo.InvariantCulture)
                .Should().BeApproximately(restLods[index].GetProperty("scale").GetDouble(), 1e-4);
        }
    }

    private static void AssertHistograms(XElement parent, JsonElement[] expected)
    {
        var histograms = parent.Elements().Where(element => element.Name.LocalName == "RasterHistogram").ToArray();
        histograms.Should().HaveCount(expected.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            Child(histograms[index], "Size").Value.Should().Be(
                expected[index].GetProperty("size").GetInt32().ToString(CultureInfo.InvariantCulture));
            double.Parse(Child(histograms[index], "Min").Value, CultureInfo.InvariantCulture)
                .Should().BeApproximately(expected[index].GetProperty("min").GetDouble(), 1e-9);
            double.Parse(Child(histograms[index], "Max").Value, CultureInfo.InvariantCulture)
                .Should().BeApproximately(expected[index].GetProperty("max").GetDouble(), 1e-9);
            var counts = Child(histograms[index], "Counts").Elements()
                .Select(element => long.Parse(element.Value, CultureInfo.InvariantCulture))
                .ToArray();
            counts.Should().Equal(expected[index].GetProperty("counts").EnumerateArray().Select(count => count.GetInt64()));
        }
    }

    private static void AssertKeyProperties(XDocument document, JsonElement json)
    {
        Property(document, "LowCellSize").Should().Be(json.GetProperty("LowCellSize").GetDouble());
        Property(document, "HighCellSize").Should().Be(json.GetProperty("HighCellSize").GetDouble());
        Property(document, "MaxCellSize").Should().Be(json.GetProperty("MaxCellSize").GetDouble());
        PropertyText(document, "BandDefinitionKeyword").Should().Be(json.GetProperty("BandDefinitionKeyword").GetString());
        PropertyText(document, "ConfigKeyword").Should().Be(json.GetProperty("ConfigKeyword").GetString());
        PropertyText(document, "DataType").Should().Be(json.GetProperty("DataType").GetString());
        int.Parse(PropertyText(document, "BandCount"), CultureInfo.InvariantCulture)
            .Should().Be(json.GetProperty("BandCount").GetInt32());
        var bands = json.GetProperty("BandProperties").EnumerateArray().ToArray();
        var soapBands = document.Descendants()
            .Where(element => element.Name.LocalName == "Key" && element.Value == "BandName")
            .Select(element => element.Parent!)
            .ToArray();
        soapBands.Should().HaveCount(bands.Length);
        for (var index = 0; index < bands.Length; index++)
        {
            Child(soapBands[index], "Value").Value.Should().Be(bands[index].GetProperty("BandName").GetString());
            var pixelType = soapBands[index].Parent!.Elements()
                .Single(element => element.Elements().Any(child => child.Name.LocalName == "Key" && child.Value == "PixelType"));
            Child(pixelType, "Value").Value.Should().Be(bands[index].GetProperty("PixelType").GetString());
        }
    }

    private static double Property(XDocument document, string key)
        => double.Parse(PropertyText(document, key), CultureInfo.InvariantCulture);

    private static string PropertyText(XDocument document, string key)
    {
        var property = document.Descendants().Single(element =>
            element.Name.LocalName == "PropertySetProperty"
            && element.Elements().Any(child => child.Name.LocalName == "Key" && child.Value == key)
            && element.Parent?.Elements().Any(child => child.Name.LocalName == "Key" && child.Value == "BandProperties") != true);
        return property.Elements().Single(element => element.Name.LocalName == "Value").Value;
    }

    private static int IntValue(XDocument document, string name)
        => int.Parse(Element(document, name).Value, CultureInfo.InvariantCulture);

    private static int[] FidSet(XDocument document)
        => Element(document, "FIDArray").Elements()
            .Select(element => int.Parse(element.Value, CultureInfo.InvariantCulture))
            .ToArray();

    private static int[] ObjectIds(JsonElement json)
        => json.GetProperty("objectIds").EnumerateArray().Select(value => value.GetInt32()).ToArray();

    private static long UnixMilliseconds(XElement parent, string name)
        => new DateTimeOffset(DateTime.Parse(
            Child(parent, name).Value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)).ToUnixTimeMilliseconds();

    private static XElement Element(XDocument document, string name)
        => document.Descendants().Single(element => element.Name.LocalName == name);

    private static XElement Child(XElement parent, string name)
        => parent.Elements().Single(element => element.Name.LocalName == name);

    private static string ServicePath()
        => $"/rest/services/{WebAppFixture.TestServiceId}/ImageServer";

    private Task<SoapExchange> PostSoapAsync(string operation)
        => PostSoapAsync(_fixture, operation);

    private static async Task<SoapExchange> PostSoapAsync(WebAppFixture fixture, string operation)
    {
        var request = $"""
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/" xmlns:xsi="{XsiNamespace}">
              <soap:Body>{operation}</soap:Body>
            </soap:Envelope>
            """;
        using var content = new StringContent(request, Encoding.UTF8, "text/xml");
        using var response = await fixture.Client.PostAsync(
            $"/services/{WebAppFixture.TestServiceId}/ImageServer",
            content);
        var body = await response.Content.ReadAsStringAsync();
        var document = response.StatusCode == HttpStatusCode.OK ? XDocument.Parse(body) : new XDocument();
        return new SoapExchange(response.StatusCode, body, document);
    }

    private static string CatalogRequest(string operation, string? where, string catalogName = "Catalog")
        => $"""
            <{operation} xmlns="{ArcGisNamespace}">
              <Name>{catalogName}</Name>
              <QueryFilter>
                <WhereClause>{where}</WhereClause>
              </QueryFilter>
            </{operation}>
            """;

    private static string CatalogItemsRequest()
        => $"""
            <GetCatalogItems xmlns="{ArcGisNamespace}">
              <Name>Catalog</Name>
              <QueryFilter>
                <SubFields>OBJECTID,Name,Shape</SubFields>
                <WhereClause>{NameFilter}</WhereClause>
                <OutputSpatialReference>
                  <WKID>4326</WKID>
                  <LatestWKID>4326</LatestWKID>
                </OutputSpatialReference>
              </QueryFilter>
            </GetCatalogItems>
            """;

    private static string ImageTileRequest()
        => $"""
            <GetImageTile xmlns="{ArcGisNamespace}">
              <Level>0</Level>
              <Row>0</Row>
              <Column>0</Column>
            </GetImageTile>
            """;

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static async Task<WebAppFixture> CreateFixtureAsync(bool enableTileCache)
    {
        var rasterStore = CreateRasterStore();
        var coverageStore = CreateCoverageStore();
        var fixture = new WebAppFixture().ConfigureServices(services =>
        {
            services.RemoveAll<IRasterStore>();
            services.AddSingleton(rasterStore);
            services.RemoveAll<IMultidimensionalCoverageStore>();
            services.AddSingleton(coverageStore);
            if (enableTileCache)
            {
                services.PostConfigure<ImageServerTileMetadataOptions>(options =>
                {
                    options.Enabled = true;
                    options.MaxLevel = 2;
                });
            }
        });
        await fixture.InitializeAsync();
        return fixture;
    }

    private static IRasterStore CreateRasterStore()
    {
        var acquired = DateTimeOffset.FromUnixTimeMilliseconds(1_704_067_200_000);
        var nextDay = DateTimeOffset.FromUnixTimeMilliseconds(1_704_153_600_000);
        var created = DateTimeOffset.FromUnixTimeMilliseconds(1_704_240_000_000);
        var service = new RasterInfo
        {
            Id = 1,
            LayerId = WebAppFixture.TestLayerId,
            Name = "Test Raster RGB",
            Width = 64,
            Height = 64,
            BandCount = 3,
            PixelType = "8BUI",
            Srid = 4326,
            NoDataValue = 0,
            GeoTransform = [0, ServiceLowCellSize, 0, 0.15, 0, -ServiceHighCellSize],
            Extent = new RasterExtent { XMin = 0, YMin = 0, XMax = 0.14, YMax = 0.15, Srid = 4326 },
            AcquisitionDate = acquired,
            CreatedAt = created,
        };
        var other = new RasterInfo
        {
            Id = 3,
            LayerId = WebAppFixture.TestLayerId,
            Name = "Other Raster",
            Width = 64,
            Height = 64,
            BandCount = 1,
            PixelType = "8BUI",
            Srid = 4326,
            GeoTransform = [0, 0.01, 0, 1.28, 0, -0.02],
            Extent = new RasterExtent { XMin = 1, YMin = 1, XMax = 1.64, YMax = 2.28, Srid = 4326 },
            AcquisitionDate = nextDay,
            CreatedAt = created,
        };
        RasterInfo[] rasters = [service, other];
        var histograms = new[]
        {
            new RasterHistogram { Band = 1, BinCount = 3, Min = 0, Max = 255, Counts = HistogramCountsBand1 },
            new RasterHistogram { Band = 2, BinCount = 3, Min = 0, Max = 255, Counts = HistogramCountsBand2 },
            new RasterHistogram { Band = 3, BinCount = 3, Min = 10, Max = 240, Counts = HistogramCountsBand3 },
        };
        var statistics = Enumerable.Range(1, 3).Select(band => new RasterStatistics
        {
            Band = band,
            MinValue = 0,
            MaxValue = 255,
            MeanValue = 40,
            StandardDeviation = 12,
            ValidPixelCount = 4096,
            NoDataPixelCount = 0,
        }).ToArray();
        var tile = new RasterResult
        {
            Data = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x54, 0x49, 0x4C, 0x45],
            ContentType = "image/png",
            Width = 256,
            Height = 256,
            Srid = 3857,
        };

        var store = Substitute.For<IRasterStore>();
        store.ListRastersAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(rasters);
        store.GetPrimaryRasterInfoAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(service);
        store.GetRasterInfoAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var id = call.ArgAt<long>(1);
                foreach (var raster in rasters)
                {
                    if (raster.Id == id)
                    {
                        return raster;
                    }
                }

                return (RasterInfo?)null;
            });
        store.QueryRastersAsync(Arg.Any<int>(), Arg.Any<RasterSelectionQuery>(), Arg.Any<CancellationToken>())
            .Returns([service]);
        store.QueryCatalogAsync(Arg.Any<int>(), Arg.Any<RasterCatalogQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => RasterCatalogQueryEvaluator.EvaluateAsync(
                rasters,
                call.ArgAt<RasterCatalogQuery>(1),
                transformService: null,
                call.ArgAt<CancellationToken>(2)));
        store.GetSensorMetadataAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, RasterSensorMetadata>());
        store.GetStatisticsAsync(
                Arg.Any<int>(),
                Arg.Any<long>(),
                Arg.Any<int[]?>(),
                Arg.Any<RasterIdentifyRendering?>(),
                Arg.Any<CancellationToken>())
            .Returns(statistics);
        store.GetMosaicStatisticsAsync(
                Arg.Any<int>(),
                Arg.Any<long[]>(),
                Arg.Any<RasterMergeStrategy>(),
                Arg.Any<int[]?>(),
                Arg.Any<RasterIdentifyRendering?>(),
                Arg.Any<CancellationToken>())
            .Returns(statistics);
        store.GetHistogramsAsync(
                Arg.Any<int>(),
                Arg.Any<long>(),
                Arg.Any<int[]?>(),
                Arg.Any<int>(),
                Arg.Any<RasterIdentifyRendering?>(),
                Arg.Any<CancellationToken>())
            .Returns(histograms);
        store.GetMosaicHistogramsAsync(
                Arg.Any<int>(),
                Arg.Any<long[]>(),
                Arg.Any<RasterMergeStrategy>(),
                Arg.Any<int[]?>(),
                Arg.Any<int>(),
                Arg.Any<RasterIdentifyRendering?>(),
                Arg.Any<CancellationToken>())
            .Returns(histograms);
        store.GetImageTileAsync(
                Arg.Any<int>(),
                Arg.Any<long>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<RasterFormat>(),
                Arg.Any<CancellationToken>())
            .Returns(tile);
        store.GetMosaicImageTileAsync(
                Arg.Any<int>(),
                Arg.Any<long[]>(),
                Arg.Any<RasterMergeStrategy>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<RasterFormat>(),
                Arg.Any<CancellationToken>())
            .Returns(tile);
        return store;
    }

    private static IMultidimensionalCoverageStore CreateCoverageStore()
    {
        var metadata = new MultidimensionalCoverageMetadata
        {
            Format = MultidimensionalCoverageFormat.NetCdf4,
            Srid = 4326,
            Extent = new RasterExtent { XMin = 0, YMin = 0, XMax = 0.14, YMax = 0.15, Srid = 4326 },
            Resolution = (ServiceLowCellSize, ServiceHighCellSize),
            Temporal = new TemporalExtent(
                DateTimeOffset.FromUnixTimeMilliseconds(1_704_067_200_000),
                DateTimeOffset.FromUnixTimeMilliseconds(1_704_153_600_000),
                StepCount: 2),
            Variables =
            [
                new MultidimensionalCoverageVariable(
                    Name: "sea_surface_temperature",
                    DataType: "float32",
                    Dimensions:
                    [
                        new MultidimensionalCoverageDimension("time", 2),
                        new MultidimensionalCoverageDimension("x", 64),
                        new MultidimensionalCoverageDimension("y", 64),
                    ],
                    ChunkLayout: null,
                    Units: "degC",
                    LongName: "Sea Surface Temperature",
                    StandardName: "sea_surface_temperature",
                    NoData: null),
            ],
        };
        var registration = new MultidimensionalCoverageRegistration
        {
            Id = 11,
            LayerId = WebAppFixture.TestLayerId,
            Name = "sst",
            Format = MultidimensionalCoverageFormat.NetCdf4,
            Provider = CloudStorageProvider.AwsS3,
            Bucket = "bucket",
            ObjectKey = "sst.nc",
            Variables = ["sea_surface_temperature"],
            Metadata = metadata,
            MetadataScannedAt = DateTimeOffset.FromUnixTimeMilliseconds(1_704_067_200_000),
            CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(1_704_067_200_000),
        };
        var store = Substitute.For<IMultidimensionalCoverageStore>();
        store.ListByLayerAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<int>(0) == WebAppFixture.TestLayerId
                ? new[] { registration }
                : Array.Empty<MultidimensionalCoverageRegistration>());
        return store;
    }

    private readonly record struct SoapExchange(HttpStatusCode Status, string Body, XDocument Document);
}
