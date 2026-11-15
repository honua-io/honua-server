// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.SharedCaching;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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

    private static HttpRequestMessage CreateRangeRequest(System.Net.Http.Headers.EntityTagHeaderValue etag)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/tile");
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
        request.Headers.IfRange = new System.Net.Http.Headers.RangeConditionHeaderValue(etag);
        return request;
    }

    private sealed class TestServer(WebApplication app) : IAsyncDisposable
    {
        internal long Generation { get; set; } = 7;
        internal HttpClient Client { get; } = app.GetTestClient();

        internal static async Task<TestServer> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddETags();
            var source = Substitute.For<IConfigurationGenerationSource>();
            builder.Services.AddSingleton(source);
            var app = builder.Build();
            app.MapGet("/tile", () => Results.Bytes(new byte[] { 1, 2, 3 },
                "application/vnd.mapbox-vector-tile", enableRangeProcessing: true))
                .WithSharedCacheClass(ResponseCacheClass.Tile);
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
