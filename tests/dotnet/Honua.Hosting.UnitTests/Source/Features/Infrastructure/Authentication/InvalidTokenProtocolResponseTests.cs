// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Infrastructure.Authentication;

/// <summary>
/// A supplied-but-invalid portal token is rejected by middleware before any protocol handler
/// runs. GeoServices keeps the Esri convention (HTTP 200, body code 498); OGC protocol requests
/// must instead receive the same auth refusal their handler gives a missing credential: HTTP 401
/// with an AccessDenied exception in the request's protocol and version format, so OGC clients
/// (QGIS) recognise it as an authentication failure.
/// </summary>
[Protocol(TestProtocols.Infrastructure)]
public sealed class InvalidTokenProtocolResponseTests
{
    private const string InvalidTokenDetail = "Invalid token.";

    [UnitTheory]
    [Operation(Operations.Security)]
    [InlineData("/rest/services/arcgis_compat_protected/MapServer/WMS", "GetCapabilities")]
    [InlineData("/rest/services/arcgis_compat_protected/MapServer/WMS", "GetMap")]
    [InlineData("/ogc/services/arcgis_compat_protected/wms", "GetCapabilities")]
    public async Task Wms111_InvalidToken_Returns401AccessDeniedIn111Format(string path, string request)
    {
        var context = await RejectAsync(path, $"?SERVICE=WMS&VERSION=1.1.1&REQUEST={request}");

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        context.Response.Headers.WWWAuthenticate.ToString().Should().Be("Bearer");
        context.Response.ContentType.Should().StartWith("application/vnd.ogc.se_xml");

        var body = ReadBody(context);
        body.Should().Be(WmsServiceExceptionReport.Build("AccessDenied", InvalidTokenDetail, "1.1.1"));
        body.Should().Contain("<!DOCTYPE ServiceExceptionReport SYSTEM \"http://schemas.opengis.net/wms/1.1.1/exception_1_1_1.dtd\">");
        body.Should().Contain("<ServiceExceptionReport version=\"1.1.1\">");
        body.Should().Contain("<ServiceException code=\"AccessDenied\">Invalid token.</ServiceException>");
        body.Should().NotContain("xmlns=\"http://www.opengis.net/ogc\"");
    }

    [UnitTheory]
    [Operation(Operations.Security)]
    [InlineData("/rest/services/arcgis_compat_protected/MapServer/WMS", "GetCapabilities")]
    [InlineData("/ogc/services/arcgis_compat_protected/wms", "GetMap")]
    public async Task Wms130_InvalidToken_Returns401AccessDeniedIn130Format(string path, string request)
    {
        var context = await RejectAsync(path, $"?SERVICE=WMS&VERSION=1.3.0&REQUEST={request}");

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        context.Response.Headers.WWWAuthenticate.ToString().Should().Be("Bearer");
        context.Response.ContentType.Should().StartWith("text/xml");

        var body = ReadBody(context);
        body.Should().Be(WmsServiceExceptionReport.Build("AccessDenied", InvalidTokenDetail, "1.3.0"));
        body.Should().Contain("xmlns=\"http://www.opengis.net/ogc\"");
        body.Should().Contain("version=\"1.3.0\"");
        body.Should().Contain("<ServiceException code=\"AccessDenied\">Invalid token.</ServiceException>");
        body.Should().NotContain("<!DOCTYPE");
    }

    [UnitTest]
    [Operation(Operations.Security)]
    public async Task Wms111_InvalidToken_ExplicitXmlExceptionsFormat_UsesTextXml()
    {
        var context = await RejectAsync(
            "/rest/services/arcgis_compat_protected/MapServer/WMS",
            "?SERVICE=WMS&VERSION=1.1.1&REQUEST=GetMap&EXCEPTIONS=text/xml");

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        context.Response.ContentType.Should().StartWith("text/xml");
        ReadBody(context).Should().Contain("<ServiceExceptionReport version=\"1.1.1\">");
    }

    [UnitTest]
    [Operation(Operations.Security)]
    public async Task Wfs_InvalidToken_Returns401AccessDenied()
    {
        var context = await RejectAsync("/wfs", "?SERVICE=WFS&VERSION=2.0.0&REQUEST=GetCapabilities");

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        context.Response.Headers.WWWAuthenticate.ToString().Should().Be("Bearer");
        var body = ReadBody(context);
        body.Should().Contain("xmlns:ows=\"http://www.opengis.net/ows/1.1\"");
        body.Should().Contain("exceptionCode=\"AccessDenied\"");
    }

    [UnitTheory]
    [Operation(Operations.Security)]
    [InlineData("/rest/services/protected/MapServer/WMTS", "?SERVICE=WMTS&REQUEST=GetTile")]
    [InlineData("/rest/services/protected/MapServer/WMTS/2010/default/WebMercatorQuad/5/12/5.png", "")]
    [InlineData("/ogc/services/protected/wmts", "?SERVICE=WMTS&REQUEST=GetTile")]
    public async Task Wmts_InvalidToken_Returns401AccessDeniedBeforeHandler(string path, string query)
    {
        var context = await RejectAsync(path, query);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        context.Response.Headers.WWWAuthenticate.ToString().Should().Be("Bearer");
        AssertWmtsException(context, "AccessDenied").Should().Contain(InvalidTokenDetail);
    }

    [UnitTheory]
    [Operation(Operations.Security)]
    [InlineData(StatusCodes.Status401Unauthorized, "Authentication required.")]
    [InlineData(StatusCodes.Status401Unauthorized, "Invalid API key.")]
    [InlineData(StatusCodes.Status401Unauthorized, "Bearer token has expired.")]
    [InlineData(StatusCodes.Status403Forbidden, "Required read permission is missing.")]
    public async Task Wmts_AuthenticationRefusal_PreservesStatusAndUsesAccessDenied(int status, string detail)
    {
        var context = CreateContext("/rest/services/protected/MapServer/WMTS", "?SERVICE=WMTS&REQUEST=GetTile");
        await StandardErrorResponseFormatter.FormatError(
            context, new StandardErrorResponse(status, "Access refused", detail)).ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(status);
        context.Response.Headers.CacheControl.ToString().Should().Contain("no-store");
        AssertWmtsException(context, "AccessDenied").Should().Contain(detail);
    }

    [UnitTheory]
    [Operation(Operations.Security)]
    [InlineData("/rest/services/protected/MapServer/WMTS", StatusCodes.Status400BadRequest, "Missing LAYER.", "MissingParameterValue")]
    [InlineData("/rest/services/protected/MapServer/WMTS", StatusCodes.Status400BadRequest, "Invalid tile index.", "InvalidParameterValue")]
    [InlineData("/rest/services/protected/MapServer/WMTS", StatusCodes.Status501NotImplemented, "Unsupported operation.", "OperationNotSupported")]
    [InlineData("/rest/services/protected/MapServer/WMTS", StatusCodes.Status500InternalServerError, "Internal failure.", "NoApplicableCode")]
    [InlineData("/wfs", StatusCodes.Status400BadRequest, "Missing LAYER.", "MissingParameterValue")]
    [InlineData("/wfs", StatusCodes.Status400BadRequest, "Invalid tile index.", "InvalidParameterValue")]
    [InlineData("/wfs", StatusCodes.Status501NotImplemented, "Unsupported operation.", "OperationNotSupported")]
    [InlineData("/wfs", StatusCodes.Status500InternalServerError, "Internal failure.", "NoApplicableCode")]
    public async Task Ogc_NonAuthenticationErrors_KeepExistingCodes(string path, int status, string detail, string code)
    {
        var context = CreateContext(path, "");
        await StandardErrorResponseFormatter.FormatError(
            context, new StandardErrorResponse(status, "Request refused", detail)).ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(status);
        ReadBody(context).Should().Contain($"exceptionCode=\"{code}\"");
        context.Response.Headers.WWWAuthenticate.ToString().Should().BeEmpty();
    }

    [UnitTheory]
    [Operation(Operations.Security)]
    [InlineData(StatusCodes.Status401Unauthorized)]
    [InlineData(StatusCodes.Status403Forbidden)]
    public async Task Wmts_ExplicitProtocolCode_KeepsExistingEnvelopeContract(int status)
    {
        var context = CreateContext("/ogc/services/protected/wmts", "?SERVICE=WMTS&REQUEST=GetTile");
        await StandardErrorResponseFormatter.FormatError(
            context,
            new StandardErrorResponse(status, "Authentication required", "Tenant claim required."),
            new ErrorResponseFormatterOptions { WmtsExceptionCode = "AuthenticationRequired" }).ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(status);
        AssertWmtsException(context, "AuthenticationRequired");
    }

    [UnitTheory]
    [Operation(Operations.Security)]
    [InlineData("/rest/services/arcgis_compat_protected/FeatureServer/0/query", "?f=json&where=1%3D1")]
    [InlineData("/rest/services/arcgis_compat_protected/MapServer", "?f=json")]
    public async Task GeoServicesRest_InvalidToken_Retains200And498(string path, string query)
    {
        var context = await RejectAsync(path, query);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        using var document = JsonDocument.Parse(ReadBody(context));
        document.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(498);
    }

    [UnitTest]
    [Operation(Operations.Security)]
    public async Task WmsAlias_ExplicitProtocolCode_KeepsExistingEnvelopeContract()
    {
        // Callers that pin a WMS code (tenant denial, backpressure) keep their published
        // contract; only the default auth mapping moved to the handler's AccessDenied response.
        var context = CreateContext("/ogc/services/example/wms", "?SERVICE=WMS&VERSION=1.1.1&REQUEST=GetMap");
        await StandardErrorResponseFormatter.FormatError(
            context,
            new StandardErrorResponse(StatusCodes.Status401Unauthorized, "Authentication Required", "Tenant claim required."),
            new ErrorResponseFormatterOptions { WmsExceptionCode = "AuthenticationRequired" }).ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        ReadBody(context).Should().Contain("code=\"AuthenticationRequired\"");
    }

    private static async Task<DefaultHttpContext> RejectAsync(string path, string query)
    {
        var context = CreateContext(path, query);
        context.Request.Headers.Authorization = "Bearer invalid.token.value";
        context.Items[PortalTokenAuthenticationExtensions.AuthenticationFailureKey] = true;

        var app = new ApplicationBuilder(context.RequestServices);
        app.UsePortalTokenAuthenticationRejection();
        app.Run(_ => throw new InvalidOperationException("An invalid token must not reach the protocol handler."));
        await app.Build()(context);
        return context;
    }

    private static DefaultHttpContext CreateContext(string path, string query)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider(),
        };
        context.Request.Method = HttpMethods.Get;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("api.example.com");
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        return reader.ReadToEnd();
    }

    private static string AssertWmtsException(HttpContext context, string code)
    {
        context.Response.ContentType.Should().StartWith("application/xml");
        XNamespace ows = "http://www.opengis.net/ows/1.1";
        var body = ReadBody(context);
        var report = XDocument.Parse(body).Root
            ?? throw new InvalidOperationException("WMTS exception report requires an XML root.");
        report.Name.Should().Be(ows + "ExceptionReport");
        report.Elements(ows + "Exception").Should().ContainSingle()
            .Which.Attribute("exceptionCode")!.Value.Should().Be(code);
        return body;
    }
}
