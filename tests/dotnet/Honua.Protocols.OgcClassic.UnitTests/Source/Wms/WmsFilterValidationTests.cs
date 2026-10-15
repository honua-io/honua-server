// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Honua.Protocols.Ogc.Classic.Wms;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wms;

/// <summary>
/// WMS FILTER client mistakes are InvalidParameterValue, and a namespaced ValueReference
/// resolves to the public field the way WFS does (#6183).
/// </summary>
[Protocol(TestProtocols.Wms13)]
public sealed class WmsFilterValidationTests
{
    private const string UnknownFieldFilter =
        "<fes:Filter xmlns:fes=\"http://www.opengis.net/fes/2.0\"><fes:PropertyIsEqualTo><fes:ValueReference>nosuchfield</fes:ValueReference><fes:Literal>x</fes:Literal></fes:PropertyIsEqualTo></fes:Filter>";

    private const string NamespacedFilter =
        "<fes:Filter xmlns:fes=\"http://www.opengis.net/fes/2.0\"><fes:PropertyIsEqualTo><fes:ValueReference>cite:population</fes:ValueReference><fes:Literal>1</fes:Literal></fes:PropertyIsEqualTo></fes:Filter>";

    private const string MistypedLiteralFilter =
        "<fes:Filter xmlns:fes=\"http://www.opengis.net/fes/2.0\"><fes:PropertyIsEqualTo><fes:ValueReference>population</fes:ValueReference><fes:Literal>abc</fes:Literal></fes:PropertyIsEqualTo></fes:Filter>";

    private const string HiddenFieldFilter =
        "<fes:Filter xmlns:fes=\"http://www.opengis.net/fes/2.0\"><fes:PropertyIsEqualTo><fes:ValueReference>category</fes:ValueReference><fes:Literal>x</fes:Literal></fes:PropertyIsEqualTo></fes:Filter>";

    private readonly ISqlFilterTranslator _sql = Substitute.For<ISqlFilterTranslator>();
    private FilterExpression? _translated;

    public WmsFilterValidationTests()
    {
        _sql.Translate(Arg.Any<FilterExpression>(), Arg.Any<MetadataV2Resource>())
            .Returns(call =>
            {
                _translated = call.Arg<FilterExpression>();
                return new SqlFragment("TRUE", []);
            });
    }

    [UnitTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetFeatureInfo")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task UnknownProperty_ReturnsInvalidParameterValue()
    {
        var (status, body) = await TranslateAsync(UnknownFieldFilter);

        status.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("InvalidParameterValue").And.Contain("nosuchfield");
        body.Should().NotContain("NoApplicableCode");
        _translated.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetFeatureInfo")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public void NamespacedValueReference_ResolvesPublicField()
    {
        var (filter, error) = Translate(NamespacedFilter);

        error.Should().BeNull();
        filter.Should().NotBeNull();
        var comparison = _translated.Should().BeOfType<BinaryExpression>().Subject;
        comparison.Left.Should().BeOfType<PropertyReference>()
            .Which.PropertyName.Should().Be("population");
    }

    [UnitTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetFeatureInfo")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task MistypedLiteral_ReturnsInvalidParameterValue()
    {
        var (status, body) = await TranslateAsync(MistypedLiteralFilter);

        status.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("InvalidParameterValue").And.Contain("numeric");
        body.Should().NotContain("NoApplicableCode");
    }

    [UnitTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetFeatureInfo")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public void GeometryWithoutSrsName_UsesLayerCrs()
    {
        const string filter =
            "<fes:Filter xmlns:fes=\"http://www.opengis.net/fes/2.0\" xmlns:gml=\"http://www.opengis.net/gml/3.2\">" +
            "<fes:BBOX><fes:ValueReference>shape</fes:ValueReference>" +
            "<gml:Envelope><gml:lowerCorner>-1000 -1000</gml:lowerCorner><gml:upperCorner>1000 1000</gml:upperCorner></gml:Envelope>" +
            "</fes:BBOX></fes:Filter>";

        var (parsed, error) = Translate(filter, Layer(3857));

        error.Should().BeNull();
        parsed.Should().NotBeNull();
        GeometrySrid(_translated).Should().Be(3857);
    }

    [UnitTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetFeatureInfo")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public void GeometryWithSrsName_KeepsDeclaredCrs()
    {
        const string filter =
            "<fes:Filter xmlns:fes=\"http://www.opengis.net/fes/2.0\" xmlns:gml=\"http://www.opengis.net/gml/3.2\">" +
            "<fes:BBOX><fes:ValueReference>shape</fes:ValueReference>" +
            "<gml:Envelope srsName=\"EPSG:4326\"><gml:lowerCorner>0 0</gml:lowerCorner><gml:upperCorner>1 1</gml:upperCorner></gml:Envelope>" +
            "</fes:BBOX></fes:Filter>";

        var (_, error) = Translate(filter, Layer(3857));

        error.Should().BeNull();
        GeometrySrid(_translated).Should().Be(4326);
    }

    [UnitTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetFeatureInfo")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task HiddenField_IsRejectedAsUnknown()
    {
        var (status, body) = await TranslateAsync(HiddenFieldFilter);

        status.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("InvalidParameterValue").And.Contain("category");
        body.Should().NotContain("NoApplicableCode");
    }

    private (SqlFragment? Filter, IResult? Error) Translate(string filter)
        => Translate(filter, Layer(null));

    private (SqlFragment? Filter, IResult? Error) Translate(string filter, MetadataV2Resource resource)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().BuildServiceProvider()
        };
        context.Request.QueryString = new QueryString("?VERSION=1.3.0");
        return WmsRequestHandlers.TranslateWmsLayerFilter(
            context,
            new FilterExpressionService(new FilterExpressionTranslator(_sql)),
            resource,
            filter);
    }

    private static int GeometrySrid(FilterExpression? expression)
    {
        var spatial = expression.Should().BeOfType<SpatialPredicate>().Subject;
        return spatial.Right.Should().BeOfType<GeometryLiteral>().Subject.Srid;
    }

    private async Task<(int Status, string Body)> TranslateAsync(string filter)
    {
        var (_, error) = Translate(filter);
        error.Should().NotBeNull();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().BuildServiceProvider()
        };
        httpContext.Response.Body = new MemoryStream();
        await error!.ExecuteAsync(httpContext);
        httpContext.Response.Body.Position = 0;
        using var reader = new StreamReader(httpContext.Response.Body, Encoding.UTF8);
        return (httpContext.Response.StatusCode, await reader.ReadToEndAsync());
    }

    private static MetadataV2Resource Layer(int? srid)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-layer-0", Name = "Test Layer" },
            Type = MetadataV2ResourceType.FeatureDataset,
            Spatial = srid is null
                ? null
                : new MetadataV2ResourceSpatial
                {
                    SpatialReference = new MetadataV2SpatialReference
                    {
                        Srid = srid.Value,
                        Crs = $"EPSG:{srid.Value}",
                        IsGeographic = srid.Value == 4326
                    },
                    GeometryType = MetadataV2GeometryType.Point,
                    PrimaryGeometryField = "shape"
                },
            SchemaFields =
            [
                new MetadataV2Field { Name = "category", Type = MetadataV2FieldType.String, Hidden = true },
                new MetadataV2Field { Name = "population", Type = MetadataV2FieldType.Integer },
                new MetadataV2Field { Name = "shape", Type = MetadataV2FieldType.Geometry }
            ]
        };
}
