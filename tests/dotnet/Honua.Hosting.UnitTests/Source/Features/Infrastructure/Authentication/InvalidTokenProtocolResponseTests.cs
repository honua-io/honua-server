// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
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
}
