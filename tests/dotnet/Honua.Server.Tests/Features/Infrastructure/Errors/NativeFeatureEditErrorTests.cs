// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Infrastructure.Models;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Infrastructure.Errors;

public sealed class NativeFeatureEditErrorTests
{
    [UnitTheory]
    [InlineData("/rest/services/example/FeatureServer/4/addFeatures", 401, 499)]
    [InlineData("/rest/services/example/FeatureServer/4/updateFeatures", 403, 403)]
    [InlineData("/rest/services/example/FeatureServer/4/deleteFeatures", 400, 400)]
    [InlineData("/rest/services/example/FeatureServer/4/applyEdits", 500, 500)]
    [InlineData("/rest/services/example/FeatureServer/applyEdits", 401, 499)]
    [InlineData("/arcgis/rest/services/example/FeatureServer/4/addFeatures", 403, 403)]
    [InlineData("/rest/services/folder/example/featureserver/4/UPDATEFEATURES", 400, 400)]
    [InlineData("/rest/services/example/FeatureServer/4/addFeatures/", 401, 499)]
    public async Task RejectedEdit_PreservesErrorEnvelopeAndReturnsFailureStatus(string path, int status, int bodyCode)
    {
        using var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = path;
        if (path.StartsWith("/arcgis/", StringComparison.Ordinal))
        {
            // The configured path-prefix middleware moves this prefix to PathBase.
            context.Request.PathBase = "/arcgis";
            context.Request.Path = path["/arcgis".Length..];
        }
        context.Request.Method = HttpMethods.Post;
        context.Response.Body = new MemoryStream();

        var error = new StandardErrorResponse(status, "Rejected", "Edit was rejected.");
        await StandardErrorResponseFormatter.FormatError(context, error).ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(status, "native editors must not mistake a rejected write for a successful save");
        context.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(context.Response.Body);
        body.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(bodyCode);
        if (status is 401 or 403)
        {
            context.Response.Headers.CacheControl.ToString().Should().Contain("no-store");
        }
    }

    [UnitTheory]
    [InlineData("POST", "/rest/services/example/FeatureServer/4/query")]
    [InlineData("GET", "/rest/services/example/FeatureServer/4/addFeatures")]
    [InlineData("POST", "/rest/services/example/MapServer/4/query")]
    public async Task NonEditRequest_RetainsExistingEnvelopeTransport(string method, string path)
    {
        using var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        await StandardErrorResponseFormatter.FormatError(context, StandardErrorResponse.Unauthorized("Token required.")).ExecuteAsync(context);
        context.Response.StatusCode.Should().Be(200);
    }

    [UnitTest]
    public async Task WfsXmlTransactionError_UsesParsedBodyVersion()
    {
        using var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = "/wfs";
        context.Request.Method = HttpMethods.Post;
        context.Items[StandardErrorResponseFormatter.WfsRequestVersionItemKey] = "1.0.0";
        context.Response.Body = new MemoryStream();
        await StandardErrorResponseFormatter.FormatError(context, StandardErrorResponse.Unauthorized("Token required.")).ExecuteAsync(context);
        context.Response.StatusCode.Should().Be(401);
        context.Response.Body.Position = 0;
        var xml = await System.Xml.Linq.XDocument.LoadAsync(context.Response.Body, System.Xml.Linq.LoadOptions.None, CancellationToken.None);
        xml.Root!.Name.LocalName.Should().Be("ServiceExceptionReport");
    }
}
