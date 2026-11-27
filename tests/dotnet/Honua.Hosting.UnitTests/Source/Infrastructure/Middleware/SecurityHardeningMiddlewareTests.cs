// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Validation;
using Honua.Core.Features.Metadata.Caching;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Security;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Infrastructure.Middleware;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class SecurityHardeningMiddlewareTests
{
    [UnitTest]
    public async Task Limits_ReadOnlyMetadataPost_EnforcesConfiguredReadOnlyCeiling()
    {
        var limits = new LimitsOptions();
        var called = false;
        var middleware = new LimitsEnforcementMiddleware(
            _ => { called = true; return Task.CompletedTask; },
            NullLogger<LimitsEnforcementMiddleware>.Instance,
            Options.Create(limits));
        var context = CreatePostContext("/stac/search", "application/json", limits.Query.MaxReadOnlyPayloadSize + 1);
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(ReadOnlyRequestMetadata.Instance, new AllowAnonymousAttribute()),
            "stac-search"));

        await middleware.InvokeAsync(context);

        called.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
    }

    [UnitTest]
    public async Task Limits_AnonymousNonEditPost_UsesReadOnlyCeiling()
    {
        var limits = new LimitsOptions();
        var called = false;
        var middleware = new LimitsEnforcementMiddleware(
            _ => { called = true; return Task.CompletedTask; },
            NullLogger<LimitsEnforcementMiddleware>.Instance,
            Options.Create(limits));
        var context = CreatePostContext("/wfs", "application/xml", limits.Query.MaxReadOnlyPayloadSize + 1);
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new AllowAnonymousAttribute()),
            "wfs"));

        await middleware.InvokeAsync(context);

        called.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
    }

    [UnitTest]
    public async Task Limits_ApplyEditsPost_KeepsEditCeiling()
    {
        var limits = new LimitsOptions();
        var called = false;
        var middleware = new LimitsEnforcementMiddleware(
            _ => { called = true; return Task.CompletedTask; },
            NullLogger<LimitsEnforcementMiddleware>.Instance,
            Options.Create(limits));
        var context = CreatePostContext(
            "/rest/services/test/FeatureServer/0/applyEdits",
            "application/x-www-form-urlencoded",
            limits.Query.MaxReadOnlyPayloadSize + 1024);
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new AllowAnonymousAttribute()),
            "applyEdits"));

        await middleware.InvokeAsync(context);

        called.Should().BeTrue();
    }

    [UnitTest]
    public async Task InputValidation_MultipartWfsDeepFilterXml_Returns400()
    {
        var called = false;
        var middleware = CreateInputValidationMiddleware(() => called = true);
        var depth = RemoteXmlDocumentParser.DefaultMaxElementDepth + 20;
        var deepFilter = string.Concat(Enumerable.Repeat("<a>", depth)) +
                         "x" +
                         string.Concat(Enumerable.Repeat("</a>", depth));
        var context = CreateMultipartContext("/wfs", [("service", "WFS"), ("FILTER", deepFilter)]);
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(new AllowAnonymousAttribute()), "wfs"));

        await middleware.InvokeAsync(context);

        called.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [UnitTest]
    public async Task InputValidation_MultipartStreamingImportRoute_SkipsFormBuffering()
    {
        var called = false;
        var middleware = CreateInputValidationMiddleware(() => called = true);
        var context = CreateMultipartContext("/api/v1/admin/import/upload", [("table", "parcels")]);
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, EndpointMetadataCollection.Empty, "import"));

        await middleware.InvokeAsync(context);

        called.Should().BeTrue();
        context.Features.Get<IFormFeature>()?.Form.Should().BeNull();
        context.Request.Body.Position.Should().Be(0);
    }

    [UnitTheory]
    [InlineData("/api/v1/admin/scenes/ingest/archive")]
    [InlineData("/api/v1/admin/scenes/ingest/model")]
    [InlineData("/api/v1/admin/geocoding/reference-data/import")]
    [InlineData("/api/v1/admin/forms/import")]
    [InlineData("/rest/services/test/FeatureServer/0/1/addAttachment")]
    public async Task InputValidation_AuthenticatedHandlerOwnedMultipartUpload_SkipsFormBuffering(string path)
    {
        var called = false;
        var middleware = CreateInputValidationMiddleware(() => called = true);
        var context = CreateMultipartContext(path, [("file", "payload")]);
        context.Request.Headers["X-API-Key"] = "test-admin-key";
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new AuthorizeAttribute()),
            "handler-upload"));

        await middleware.InvokeAsync(context);

        called.Should().BeTrue();
        context.Features.Get<IFormFeature>()?.Form.Should().BeNull();
        context.Request.Body.Position.Should().Be(0);
    }

    [UnitTest]
    public async Task InputValidation_MultipartEndpointAcceptingFormFile_SkipsFormBuffering()
    {
        var called = false;
        var middleware = CreateInputValidationMiddleware(() => called = true);
        var context = CreateMultipartContext("/api/v1/custom/upload", [("file", "payload")]);
        context.Request.Headers["X-API-Key"] = "test-admin-key";
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new AuthorizeAttribute(), new AcceptsFormFileMetadata()),
            "accepts-form-file"));

        await middleware.InvokeAsync(context);

        called.Should().BeTrue();
        context.Features.Get<IFormFeature>()?.Form.Should().BeNull();
        context.Request.Body.Position.Should().Be(0);
    }

    private sealed class AcceptsFormFileMetadata : IAcceptsMetadata
    {
        public IReadOnlyList<string> ContentTypes { get; } = ["multipart/form-data"];

        public Type? RequestType => typeof(IFormFile);

        public bool IsOptional => false;
    }

    [UnitTest]
    public async Task InputValidation_UnauthenticatedAuthorizedEndpointLargeForm_SkipsPreAuthBuffering()
    {
        var called = false;
        var middleware = CreateInputValidationMiddleware(() => called = true);
        var context = CreatePostContext("/api/v1/admin/items", "application/x-www-form-urlencoded", 2_000_000);
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("a=b"));
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new AuthorizeAttribute()),
            "admin-items"));

        await middleware.InvokeAsync(context);

        called.Should().BeTrue();
        context.Features.Get<IFormFeature>()?.Form.Should().BeNull();
        context.Request.Body.Position.Should().Be(0);
    }

    private static InputValidationMiddleware CreateInputValidationMiddleware(Action onNext)
        => new(
            _ => { onNext(); return Task.CompletedTask; },
            NullLogger<InputValidationMiddleware>.Instance,
            Options.Create(new InputValidationOptions()),
            Options.Create(new LimitsOptions()));

    private static DefaultHttpContext CreatePostContext(string path, string contentType, long contentLength)
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

    private static DefaultHttpContext CreateMultipartContext(string path, (string Name, string Value)[] fields)
    {
        const string boundary = "test-boundary-123";
        var body = new StringBuilder();
        foreach (var (name, value) in fields)
        {
            body.Append("--").Append(boundary).Append("\r\n");
            body.Append("Content-Disposition: form-data; name=\"").Append(name).Append("\"\r\n\r\n");
            body.Append(value).Append("\r\n");
        }

        body.Append("--").Append(boundary).Append("--\r\n");
        var bytes = Encoding.UTF8.GetBytes(body.ToString());
        var context = CreatePostContext(path, $"multipart/form-data; boundary={boundary}", bytes.Length);
        context.Request.Body = new MemoryStream(bytes);
        return context;
    }
}
