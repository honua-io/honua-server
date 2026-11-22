// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Validation;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Rendering;
using Honua.Infrastructure.Services;
using Honua.Infrastructure.Validation;
using Honua.Protocols.Ogc.Classic;
using Honua.Protocols.Ogc.Classic.Wms;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wms;

[Protocol(TestProtocols.Wms13)]
public sealed class WmsCqlAndHtmlTests
{
    private readonly ISqlFilterTranslator _sql = Substitute.For<ISqlFilterTranslator>();
    private FilterExpression? _translated;

    public WmsCqlAndHtmlTests()
    {
        _sql.Translate(Arg.Any<FilterExpression>(), Arg.Any<MetadataV2Object>())
            .Returns(call =>
            {
                _translated = call.Arg<FilterExpression>();
                return new SqlFragment("population > 100", []);
            });
    }

    [UnitTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public void CqlFilter_ValidPredicate_TranslatesUsingCql2Parser()
    {
        var (filter, error) = TranslateCql("population > 100 AND name = 'Honolulu'");

        error.Should().BeNull();
        filter.Should().NotBeNull();
        _translated.Should().BeOfType<BinaryExpression>().Subject.Operator.Should().Be(BinaryOperator.And);
    }

    [UnitTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task CqlFilter_InvalidSyntax_ReturnsInvalidParameterValueException()
    {
        var (status, body) = await TranslateCqlAsync("population >>>");

        status.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("InvalidParameterValue").And.Contain("CQL_FILTER");
        _translated.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task CqlFilter_HiddenOrUnknownField_ReturnsInvalidParameterValueException()
    {
        var (hiddenStatus, hiddenBody) = await TranslateCqlAsync("secret_code = 'abc'");
        hiddenStatus.Should().Be(StatusCodes.Status400BadRequest);
        hiddenBody.Should().Contain("InvalidParameterValue");

        var (unknownStatus, unknownBody) = await TranslateCqlAsync("does_not_exist = 1");
        unknownStatus.Should().Be(StatusCodes.Status400BadRequest);
        unknownBody.Should().Contain("InvalidParameterValue");
    }

    [UnitTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public void SplitCqlFilterTokens_PreservesSemicolonsInsideQuotes()
    {
        OgcClassicCqlFilter.SplitTokens("name = 'a;b';population > 10")
            .Should().Equal("name = 'a;b'", "population > 10");
        OgcClassicCqlFilter.SplitTokens("name = 'it''s;here' ; label = \"x;y\"")
            .Should().Equal("name = 'it''s;here'", "label = \"x;y\"");
    }

    [UnitTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetFeatureInfo")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public void PopupHtmlFormatter_RendersPopupTitleDescriptionAndVisibleFields_AndHtmlEncodesValues()
    {
        using var popupDoc = JsonDocument.Parse("""
            {
              "title": "Parcel: {name} <script>alert(1)</script>",
              "description": "Population is {population}",
              "fieldInfos": [
                { "fieldName": "name", "label": "Display Name", "visible": true },
                { "fieldName": "population", "label": "Pop", "visible": true },
                { "fieldName": "hidden_by_popup", "label": "Hidden", "visible": false },
                { "fieldName": "secret_code", "label": "Secret", "visible": true }
              ]
            }
            """);

        var resource = CreateResource(popupInfo: popupDoc.RootElement.Clone());
        var attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = "Honolulu & <Maui>",
            ["population"] = 350000,
            ["hidden_by_popup"] = "do-not-show"
        };

        var html = WmsPopupHtmlFormatter.BuildHtml([new WmsPopupHtmlFormatter.FeatureEntry("parcels", resource, attributes)]);

        html.Should().Contain("<h2>Parcel: Honolulu &amp; &lt;Maui&gt; &lt;script&gt;alert(1)&lt;/script&gt;</h2>");
        html.Should().Contain("<p class=\"honua-popup-description\">Population is 350000</p>");
        html.Should().Contain("<tr><th>Display Name</th><td>Honolulu &amp; &lt;Maui&gt;</td></tr>");
        html.Should().Contain("<tr><th>Pop</th><td>350000</td></tr>");
        html.Should().NotContain("do-not-show").And.NotContain("Secret").And.NotContain("<script>");
    }

    [UnitTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetFeatureInfo")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public void TryNormalizeFeatureInfoFormat_GatesHtmlToWmsOnly()
    {
        OgcClassicRequestHelpers.TryNormalizeFeatureInfoFormat("text/html", out _).Should().BeFalse();
        OgcClassicRequestHelpers.NormalizeWmsFeatureInfoFormat("TEXT/HTML").Should().Be("text/html");
        OgcClassicRequestHelpers.NormalizeWmsFeatureInfoFormat("application/json").Should().Be("application/json");
        OgcClassicRequestHelpers.NormalizeWmsFeatureInfoFormat("image/png").Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetFeatureInfo")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task HandleWms_GetFeatureInfo_HtmlAndCqlFilter_BehaveAsExpected()
    {
        using var popupDoc = JsonDocument.Parse("""{"title":"City: {name}","fieldInfos":[{"fieldName":"population","label":"Population","visible":true}]}""");
        await using var sp = BuildWmsServices(CreateResource(popupInfo: popupDoc.RootElement.Clone()), CreateSampleFeatureReader(), _sql);

        var (htmlStatus, htmlType, htmlBytes) = await ExecuteWmsAsync(
            sp, "?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetFeatureInfo&LAYERS=parcels&QUERY_LAYERS=parcels&STYLES=&CRS=CRS:84&BBOX=-1,-1,1,1&WIDTH=64&HEIGHT=64&I=32&J=32&INFO_FORMAT=text/html");
        htmlStatus.Should().Be(StatusCodes.Status200OK);
        htmlType.Should().StartWith("text/html");
        Encoding.UTF8.GetString(htmlBytes).Should().Contain("<h2>City: Honolulu</h2>").And.Contain("<tr><th>Population</th><td>350000</td></tr>");

        var (cqlStatus, _, _) = await ExecuteWmsAsync(
            sp, "?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetFeatureInfo&LAYERS=parcels&QUERY_LAYERS=parcels&STYLES=&CRS=CRS:84&BBOX=-1,-1,1,1&WIDTH=64&HEIGHT=64&I=32&J=32&INFO_FORMAT=text/html&CQL_FILTER=population+%3E+100000");
        cqlStatus.Should().Be(StatusCodes.Status200OK);

        var (badCqlStatus, _, badCqlBytes) = await ExecuteWmsAsync(
            sp, "?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetFeatureInfo&LAYERS=parcels&QUERY_LAYERS=parcels&STYLES=&CRS=CRS:84&BBOX=-1,-1,1,1&WIDTH=64&HEIGHT=64&I=32&J=32&INFO_FORMAT=text/html&CQL_FILTER=population+%3E");
        badCqlStatus.Should().Be(StatusCodes.Status400BadRequest);
        Encoding.UTF8.GetString(badCqlBytes).Should().Contain("InvalidParameterValue");
    }

    internal static IFeatureReader CreateSampleFeatureReader(Action<FeatureQuery>? onQuery = null)
    {
        var pointWkb = new WKBWriter().Write(new Point(0, 0));
        var feature = new Feature
        {
            Id = 1,
            Geometry = pointWkb,
            Attributes = ImmutableDictionary<string, object?>.Empty.Add("name", "Honolulu").Add("population", 350000)
        };
        var reader = Substitute.For<IFeatureReader>();
        reader.QueryAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                onQuery?.Invoke(call.Arg<FeatureQuery>());
                return ToAsyncEnumerable([feature]);
            });
        return reader;
    }

    internal static ServiceProvider BuildWmsServices(MetadataV2Object resource, IFeatureReader reader, ISqlFilterTranslator sql)
    {
        var (service, snapshot) = CreateMetadataSnapshot(resource);
        var resourceValidator = Substitute.For<IResourceValidator>();
        resourceValidator.ValidateServiceV2Async("test-service", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ResourceValidationResult.Success(service, snapshot)));

        var routeValidator = Substitute.For<IRouteParameterValidator>();
        routeValidator.ValidateServiceId(Arg.Any<HttpContext>()).Returns(ValidationResult<string>.Success("test-service"));

        var services = new ServiceCollection();
        services.AddLogging().AddOptions().AddValidationServices();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(routeValidator);
        services.AddSingleton(resourceValidator);
        services.AddSingleton<IMetadataV2GraphProvider>(new StaticGraphProvider(snapshot));
        services.AddSingleton(reader);
        services.AddSingleton(Substitute.For<ILayerStyleCatalog>());
        services.AddSingleton<IFilterExpressionService>(new FilterExpressionService(new FilterExpressionTranslator(sql)));
        services.AddSingleton<RasterRenderCapacityLimiter>();
        return services.BuildServiceProvider();
    }

    internal static async Task<(int Status, string ContentType, byte[] Body)> ExecuteWmsAsync(
        IServiceProvider serviceProvider,
        string queryString)
    {
        var context = new DefaultHttpContext { RequestServices = serviceProvider };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("example.test");
        context.Request.Path = "/rest/services/test-service/MapServer/WMS";
        context.Request.RouteValues["serviceId"] = "test-service";
        context.Request.QueryString = new QueryString(queryString);
        context.Response.Body = new MemoryStream();

        var result = await WmsRequestHandlers.HandleWms(context);
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var ms = new MemoryStream();
        await context.Response.Body.CopyToAsync(ms);
        return (context.Response.StatusCode, context.Response.ContentType ?? string.Empty, ms.ToArray());
    }

    internal static MetadataV2Object CreateResource(JsonElement? popupInfo = null)
    {
        var extensions = popupInfo.HasValue
            ? new Dictionary<string, JsonElement> { ["geoservices:popupInfo"] = popupInfo.Value }
            : new Dictionary<string, JsonElement>();

        return new MetadataV2Object
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-1", Name = "parcels", Title = "Parcels" },
            Type = MetadataV2ObjectType.FeatureDataset,
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
            AccessPolicy = new AccessPolicy { AllowAnonymous = true },
            Spatial = new MetadataV2ObjectSpatial
            {
                GeometryType = MetadataV2GeometryType.Point,
                PrimaryGeometryField = "shape",
                SpatialReference = new MetadataV2SpatialReference { Srid = 4326, Crs = "EPSG:4326", IsGeographic = true }
            },
            SchemaFields =
            [
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer },
                new MetadataV2Field { Name = "name", Alias = "Parcel Name", Type = MetadataV2FieldType.String },
                new MetadataV2Field { Name = "population", Alias = "Population", Type = MetadataV2FieldType.Integer },
                new MetadataV2Field { Name = "secret_code", Type = MetadataV2FieldType.String, Hidden = true },
                new MetadataV2Field { Name = "shape", Type = MetadataV2FieldType.Geometry }
            ],
            Extensions = extensions
        };
    }

    private static (MetadataV2Service Service, MetadataV2GraphSnapshot Snapshot) CreateMetadataSnapshot(MetadataV2Object resource)
    {
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "svc-1", Name = "test-service", Title = "Test Service" },
            Protocols = ["Wms"],
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
            AccessPolicy = new AccessPolicy { AllowAnonymous = true },
            SpatialReference = new MetadataV2SpatialReference { Srid = 4326, Crs = "EPSG:4326", IsGeographic = true }
        };
        var layer = new MetadataV2Layer
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "pub-1", Name = "parcels" },
            ServiceId = service.Metadata.Id,
            ResourceId = resource.Metadata.Id,
            LayerIndex = 1,
            ServiceLocalId = "parcels",
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active }
        };
        var graph = new MetadataV2Graph { Services = [service], Resources = [resource], Publications = [layer] };
        return (service, new MetadataV2GraphSnapshot(graph, "\"test\"", DateTimeOffset.UtcNow));
    }

    private (SqlFragment? Filter, IResult? Error) TranslateCql(string cqlFilter)
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        context.Request.QueryString = new QueryString("?VERSION=1.3.0");
        return WmsRequestHandlers.TranslateWmsLayerCqlFilter(
            context, (new FilterExpressionService(new FilterExpressionTranslator(_sql)), CreateResource()), cqlFilter);
    }

    private async Task<(int Status, string Body)> TranslateCqlAsync(string cqlFilter)
    {
        var (_, error) = TranslateCql(cqlFilter);
        error.Should().NotBeNull();
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = new MemoryStream();
        await error!.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        return (context.Response.StatusCode, await reader.ReadToEndAsync());
    }

    private static async IAsyncEnumerable<Feature> ToAsyncEnumerable(IEnumerable<Feature> features)
    {
        foreach (var f in features)
        {
            yield return f;
        }

        await Task.CompletedTask;
    }

    private sealed class StaticGraphProvider(MetadataV2GraphSnapshot snapshot) : IMetadataV2GraphProvider
    {
        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(snapshot);

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<MetadataV2GraphSnapshot?>(snapshot);
    }
}
