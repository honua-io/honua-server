// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.SharedCaching;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Hosting.ComponentTests;

[Trait("Tier", "Integration")]
public sealed class ConfigurationETagTests
{
    [Theory]
    [InlineData("/tile")]
    [InlineData("/metadata")]
    [InlineData("/query")]
    [InlineData("/text")]
    [InlineData("/json")]
    public async Task ConditionalRead_GenerationChangesWithIdenticalContent_ReturnsNewRepresentation(string path)
    {
        await using var server = await TestServer.StartAsync();
        var client = server.Client;

        using var first = await client.GetAsync(path);
        first.EnsureSuccessStatusCode();
        var original = first.Headers.ETag;
        original.Should().NotBeNull();
        using var unchangedRequest = new HttpRequestMessage(HttpMethod.Get, path);
        unchangedRequest.Headers.IfNoneMatch.Add(original!);
        using var unchanged = await client.SendAsync(unchangedRequest);
        unchanged.StatusCode.Should().Be(System.Net.HttpStatusCode.NotModified);
        unchanged.Headers.ETag.Should().Be(original);

        server.Generation = 8;
        using var changedRequest = new HttpRequestMessage(HttpMethod.Get, path);
        changedRequest.Headers.IfNoneMatch.Add(original!);
        using var changed = await client.SendAsync(changedRequest);
        changed.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        changed.Headers.ETag.Should().NotBe(original);
        changed.Headers.ETag!.Tag.Should().EndWith("-g8\"");
        (await changed.Content.ReadAsByteArrayAsync()).Should().Equal(await first.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("/tile")]
    [InlineData("/metadata")]
    [InlineData("/query")]
    [InlineData("/text")]
    [InlineData("/json")]
    public async Task ConditionalRead_WeakListAndStaleIfMatch_UsesGenerationValidator(string path)
    {
        await using var server = await TestServer.StartAsync();
        using var first = await server.Client.GetAsync(path);
        var tag = first.Headers.ETag!.Tag;
        using var weakRequest = new HttpRequestMessage(HttpMethod.Get, path);
        weakRequest.Headers.TryAddWithoutValidation("If-None-Match", $"\"other\", W/{tag}");
        using var weak = await server.Client.SendAsync(weakRequest);
        weak.StatusCode.Should().Be(System.Net.HttpStatusCode.NotModified);

        server.Generation++;
        using var staleRequest = new HttpRequestMessage(HttpMethod.Get, path);
        staleRequest.Headers.TryAddWithoutValidation("If-Match", tag);
        using var stale = await server.Client.SendAsync(staleRequest);
        stale.StatusCode.Should().Be(System.Net.HttpStatusCode.PreconditionFailed);
    }

    [Fact]
    public async Task ConditionalRead_RangeWithIfRange_RespectsGenerationChange()
    {
        await using var server = await TestServer.StartAsync();
        using var first = await server.Client.GetAsync("/tile");
        using var partialRequest = CreateRangeRequest(first.Headers.ETag!);
        using var partial = await server.Client.SendAsync(partialRequest);
        partial.StatusCode.Should().Be(System.Net.HttpStatusCode.PartialContent);
        (await partial.Content.ReadAsByteArrayAsync()).Should().Equal(1);

        server.Generation++;
        using var staleRequest = CreateRangeRequest(first.Headers.ETag!);
        using var changed = await server.Client.SendAsync(staleRequest);
        changed.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        (await changed.Content.ReadAsByteArrayAsync()).Should().Equal(1, 2, 3);
    }

    [Theory]
    [InlineData("/metadata")]
    [InlineData("/json")]
    [InlineData("/text")]
    [InlineData("/query")]
    public async Task ConditionalRead_CompressedRepresentation_UsesDistinctValidator(string path)
    {
        await using var server = await TestServer.StartAsync();
        using var identity = await server.Client.GetAsync(path);
        using var gzipRequest = new HttpRequestMessage(HttpMethod.Get, path);
        gzipRequest.Headers.AcceptEncoding.ParseAdd("gzip");
        using var gzip = await server.Client.SendAsync(gzipRequest);
        gzip.Content.Headers.ContentEncoding.Should().Contain("gzip");
        var compressed = await gzip.Content.ReadAsByteArrayAsync();
        compressed.Should().NotEqual(await identity.Content.ReadAsByteArrayAsync());
        gzip.Headers.ETag.Should().NotBe(identity.Headers.ETag);
        gzip.Headers.ETag!.IsWeak.Should().BeTrue();

        using var crossCoding = new HttpRequestMessage(HttpMethod.Get, path);
        crossCoding.Headers.AcceptEncoding.ParseAdd("gzip");
        crossCoding.Headers.IfNoneMatch.Add(identity.Headers.ETag!);
        using var changed = await server.Client.SendAsync(crossCoding);
        changed.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        changed.Headers.ETag.Should().Be(gzip.Headers.ETag);

        using var revalidate = new HttpRequestMessage(HttpMethod.Get, path);
        revalidate.Headers.AcceptEncoding.ParseAdd("gzip");
        revalidate.Headers.IfNoneMatch.Add(gzip.Headers.ETag!);
        using var unchanged = await server.Client.SendAsync(revalidate);
        unchanged.StatusCode.Should().Be(System.Net.HttpStatusCode.NotModified);
        unchanged.Headers.ETag.Should().Be(gzip.Headers.ETag);
        unchanged.Headers.Vary.Should().Contain("Accept-Encoding");

        using var identityRequest = new HttpRequestMessage(HttpMethod.Get, path);
        identityRequest.Headers.IfNoneMatch.Add(gzip.Headers.ETag!);
        using var identityChanged = await server.Client.SendAsync(identityRequest);
        identityChanged.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        identityChanged.Headers.ETag.Should().Be(identity.Headers.ETag);
    }

    [Theory]
    [InlineData("/text", GenerationRegistration.Enabled)]
    [InlineData("/text", GenerationRegistration.Disabled)]
    [InlineData("/query", GenerationRegistration.Enabled)]
    [InlineData("/query", GenerationRegistration.Disabled)]
    [InlineData("/metadata", GenerationRegistration.Enabled)]
    [InlineData("/metadata", GenerationRegistration.Disabled)]
    [InlineData("/json", GenerationRegistration.Enabled)]
    [InlineData("/json", GenerationRegistration.Disabled)]
    public async Task ConditionalRead_CompressionWithOptionalGeneration_PreservesValidatorSemantics(string path, GenerationRegistration generation)
    {
        await using var server = await TestServer.StartAsync(generation);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.AcceptEncoding.ParseAdd("gzip");
        using var first = await server.Client.SendAsync(request);
        first.Headers.ETag!.IsWeak.Should().BeTrue();

        using var precondition = new HttpRequestMessage(HttpMethod.Get, path);
        precondition.Headers.AcceptEncoding.ParseAdd("gzip");
        precondition.Headers.IfMatch.Add(first.Headers.ETag!);
        using var rejected = await server.Client.SendAsync(precondition);
        rejected.StatusCode.Should().Be(System.Net.HttpStatusCode.PreconditionFailed);
        if (generation == GenerationRegistration.Disabled)
        {
            return;
        }

        server.Generation++;
        using var revalidate = new HttpRequestMessage(HttpMethod.Get, path);
        revalidate.Headers.AcceptEncoding.ParseAdd("gzip");
        revalidate.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        using var changed = await server.Client.SendAsync(revalidate);
        changed.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        changed.Headers.ETag!.Tag.Should().EndWith("-g8\"");
        changed.Headers.ETag.Should().NotBe(first.Headers.ETag);
    }

    [Theory]
    [InlineData("gzip;q=0")]
    [InlineData("unknown")]
    [InlineData("identity;q=1, gzip;q=0.5")]
    public async Task ConditionalRead_CompressionNotSelected_RetainsIdentityValidator(string encoding)
    {
        await using var server = await TestServer.StartAsync();
        using var identity = await server.Client.GetAsync("/text");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/text");
        request.Headers.AcceptEncoding.ParseAdd(encoding);
        using var response = await server.Client.SendAsync(request);
        response.Content.Headers.ContentEncoding.Should().BeEmpty();
        response.Headers.ETag.Should().Be(identity.Headers.ETag);
    }

    [Fact]
    public async Task ConditionalRead_CompressionCandidateWithIfRange_ReturnsFullRepresentation()
    {
        await using var server = await TestServer.StartAsync();
        using var initialRequest = new HttpRequestMessage(HttpMethod.Get, "/range");
        initialRequest.Headers.AcceptEncoding.ParseAdd("gzip");
        using var first = await server.Client.SendAsync(initialRequest);
        first.Content.Headers.ContentEncoding.Should().Contain("gzip");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/range");
        request.Headers.AcceptEncoding.ParseAdd("gzip");
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
        request.Headers.TryAddWithoutValidation("If-Range", first.Headers.ETag!.ToString());
        using var response = await server.Client.SendAsync(request);
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        response.Content.Headers.ContentRange.Should().BeNull();
    }

    private static HttpRequestMessage CreateRangeRequest(System.Net.Http.Headers.EntityTagHeaderValue etag)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/tile");
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
        request.Headers.IfRange = new System.Net.Http.Headers.RangeConditionHeaderValue(etag);
        return request;
    }

    public enum GenerationRegistration
    {
        Enabled,
        Disabled
    }

    private sealed class TestServer(WebApplication app) : IAsyncDisposable
    {
        internal long Generation { get; set; } = 7;
        internal HttpClient Client { get; } = app.GetTestClient();

        internal static async Task<TestServer> StartAsync(GenerationRegistration generation = GenerationRegistration.Enabled)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddETags();
            builder.Services.AddResponseCompression(options => options.Providers.Add<GzipCompressionProvider>());
            var source = Substitute.For<IConfigurationGenerationSource>();
            if (generation == GenerationRegistration.Enabled)
            {
                builder.Services.AddSingleton(source);
            }
            var app = builder.Build();
            app.UseResponseCompression();
            app.MapGet("/tile", () => Results.Bytes(new byte[] { 1, 2, 3 },
                "application/vnd.mapbox-vector-tile", enableRangeProcessing: true))
                .WithSharedCacheClass(ResponseCacheClass.Tile);
            app.MapGet("/range", () => Results.Bytes("unchanged"u8.ToArray(),
                "text/plain", enableRangeProcessing: true))
                .WithSharedCacheClass(ResponseCacheClass.MetadataDocument);
            app.MapGet("/metadata", () => Results.Ok("unchanged")).WithETag();
            app.MapGet("/json", () => Results.Json("unchanged"))
                .WithSharedCacheClass(ResponseCacheClass.MetadataDocument);
            app.MapGet("/text", () => Results.Text("unchanged"))
                .WithSharedCacheClass(ResponseCacheClass.MetadataDocument);
            app.MapGet("/query", (HttpContext context, IETagService tags) =>
                ResponseCacheUtilities.CreateResultFromCachedResponse(context,
                    ResponseCacheUtilities.CreateCachedResponse(new byte[] { 1, 2, 3 }, "application/json", tags), tags));
            await app.StartAsync();
            var server = new TestServer(app);
            source.GetGenerationAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(server.Generation));
            return server;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
        }
    }
}
