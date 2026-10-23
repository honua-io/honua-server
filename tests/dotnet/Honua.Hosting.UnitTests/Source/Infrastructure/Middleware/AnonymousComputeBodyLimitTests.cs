// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Security;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Infrastructure.Middleware;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class AnonymousComputeBodyLimitTests
{
    [UnitTest]
    public async Task Limits_UnmatchedImportPath_UsesEditCeiling()
    {
        var limits = new LimitsOptions();
        var called = false;
        var middleware = new LimitsEnforcementMiddleware(
            _ => { called = true; return Task.CompletedTask; },
            NullLogger<LimitsEnforcementMiddleware>.Instance,
            Options.Create(limits));
        var context = Post("/x/admin/import/upload", "application/x-www-form-urlencoded", limits.Imports.MaxImportSize);
        var feature = TrackBodyLimit(context, limits.Imports.MaxImportSize);

        await middleware.InvokeAsync(context);

        called.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        feature.MaxRequestBodySize.Should().Be(limits.Edits.MaxPayloadSize);
    }

    [UnitTest]
    public async Task Limits_MatchedMultipartImport_KeepsImportCeiling()
    {
        var limits = new LimitsOptions();
        var called = false;
        var middleware = new LimitsEnforcementMiddleware(
            _ => { called = true; return Task.CompletedTask; },
            NullLogger<LimitsEnforcementMiddleware>.Instance,
            Options.Create(limits));
        var context = Post("/api/v1/admin/import/upload", "multipart/form-data; boundary=x", limits.Imports.MaxImportSize);
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, EndpointMetadataCollection.Empty, "import"));
        var feature = TrackBodyLimit(context, null);

        await middleware.InvokeAsync(context);

        called.Should().BeTrue();
        feature.MaxRequestBodySize.Should().Be(limits.Imports.MaxImportSize);
    }

    [UnitTest]
    public async Task Limits_MatchedUrlEncodedImport_UsesEditCeiling()
    {
        var limits = new LimitsOptions();
        var called = false;
        var middleware = new LimitsEnforcementMiddleware(
            _ => { called = true; return Task.CompletedTask; },
            NullLogger<LimitsEnforcementMiddleware>.Instance,
            Options.Create(limits));
        var context = Post(
            "/api/v1/admin/import/upload",
            "application/x-www-form-urlencoded",
            limits.Edits.MaxPayloadSize + 1);
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, EndpointMetadataCollection.Empty, "import"));

        await middleware.InvokeAsync(context);

        called.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
    }

    [UnitTest]
    public async Task Limits_UnmatchedSmallBody_Continues()
    {
        var limits = new LimitsOptions();
        var called = false;
        var middleware = new LimitsEnforcementMiddleware(
            _ => { called = true; return Task.CompletedTask; },
            NullLogger<LimitsEnforcementMiddleware>.Instance,
            Options.Create(limits));
        var context = Post("/x/admin/import/upload", "application/x-www-form-urlencoded", limits.Edits.MaxPayloadSize);

        await middleware.InvokeAsync(context);

        called.Should().BeTrue();
    }

    [UnitTest]
    public async Task Clamp_AnonymousImportContentLength_Returns413AndLowersBodyLimit()
    {
        var limits = new LimitsOptions();
        var called = false;
        var middleware = new AnonymousImportPayloadClampMiddleware(
            _ => { called = true; return Task.CompletedTask; },
            Options.Create(limits));
        var context = Post("/api/v1/admin/import/upload", "multipart/form-data; boundary=x", limits.Imports.MaxImportSize);
        var feature = TrackBodyLimit(context, limits.Imports.MaxImportSize);

        await middleware.InvokeAsync(context);

        called.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        feature.MaxRequestBodySize.Should().Be(limits.Edits.MaxPayloadSize);
    }

    [UnitTest]
    public async Task Clamp_AuthenticatedImport_Continues()
    {
        var limits = new LimitsOptions();
        var called = false;
        var middleware = new AnonymousImportPayloadClampMiddleware(
            _ => { called = true; return Task.CompletedTask; },
            Options.Create(limits));
        var context = Post("/api/v1/admin/import/upload", "multipart/form-data; boundary=x", limits.Imports.MaxImportSize);
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "admin")], "Test"));
        var feature = TrackBodyLimit(context, limits.Imports.MaxImportSize);

        await middleware.InvokeAsync(context);

        called.Should().BeTrue();
        feature.MaxRequestBodySize.Should().Be(limits.Imports.MaxImportSize);
    }

    [UnitTest]
    public async Task Clamp_AnonymousAttachment_DoesNotUseImportCeiling()
    {
        var limits = new LimitsOptions();
        var called = false;
        var middleware = new AnonymousImportPayloadClampMiddleware(
            _ => { called = true; return Task.CompletedTask; },
            Options.Create(limits));
        var context = Post("/rest/services/x/FeatureServer/0/addAttachment", "application/x-www-form-urlencoded", limits.Imports.MaxImportSize);

        await middleware.InvokeAsync(context);

        called.Should().BeTrue();
    }

    [UnitTest]
    public async Task InputValidation_NoEndpoint_DoesNotParseUrlEncodedBody()
    {
        var called = false;
        var middleware = new InputValidationMiddleware(
            _ => { called = true; return Task.CompletedTask; },
            NullLogger<InputValidationMiddleware>.Instance,
            Options.Create(new InputValidationOptions()),
            Options.Create(new LimitsOptions()));
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/x/admin/import/upload";
        context.Request.ContentType = "application/x-www-form-urlencoded";
        var bytes = Encoding.UTF8.GetBytes("a=b");
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;

        await middleware.InvokeAsync(context);

        called.Should().BeTrue();
        context.Features.Get<IFormFeature>().Should().BeNull();
    }

    [UnitTest]
    public async Task InputValidation_MatchedEndpoint_ParsesUrlEncodedBody()
    {
        var called = false;
        var middleware = new InputValidationMiddleware(
            _ => { called = true; return Task.CompletedTask; },
            NullLogger<InputValidationMiddleware>.Instance,
            Options.Create(new InputValidationOptions()),
            Options.Create(new LimitsOptions()));
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/v1/admin/import/upload";
        context.Request.ContentType = "application/x-www-form-urlencoded";
        var bytes = Encoding.UTF8.GetBytes("a=b");
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, EndpointMetadataCollection.Empty, "import"));

        await middleware.InvokeAsync(context);

        called.Should().BeTrue();
        context.Request.Form["a"].ToString().Should().Be("b");
    }

    private static DefaultHttpContext Post(string path, string contentType, long contentLength)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = path;
        context.Request.ContentType = contentType;
        context.Request.ContentLength = contentLength;
        context.Response.Body = new MemoryStream();
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        return context;
    }

    private static MutableBodyLimitFeature TrackBodyLimit(HttpContext context, long? current)
    {
        var feature = new MutableBodyLimitFeature { MaxRequestBodySize = current };
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);
        return feature;
    }

    private sealed class MutableBodyLimitFeature : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;

        public long? MaxRequestBodySize { get; set; }
    }
}
