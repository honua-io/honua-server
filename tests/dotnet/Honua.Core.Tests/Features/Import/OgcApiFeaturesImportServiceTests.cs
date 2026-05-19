// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Net;
using FluentAssertions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Core.Tests.Features.Import;

public sealed class OgcApiFeaturesImportServiceTests
{
    [Fact]
    public async Task ImportCollectionAsync_WithPagedFixtureSource_WritesAllFeaturesIdempotently()
    {
        var handler = new FixtureItemsHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://demo.example") };
        var sink = new RecordingSink();
        var service = CreateService(httpClient, sink);

        var first = await service.ImportCollectionAsync(NewRequest());
        var second = await service.ImportCollectionAsync(NewRequest());

        first.Success.Should().BeTrue();
        first.CollectionId.Should().Be("roads");
        first.Target.Should().Be("test_schema.roads");
        first.FeaturesImported.Should().Be(3);
        first.FeaturesSkipped.Should().Be(0);
        first.PagesFetched.Should().Be(2);
        first.Truncated.Should().BeFalse();

        sink.EnsureTargetCalls.Should().BeGreaterThanOrEqualTo(1);
        sink.WrittenFeatures.Select(static f => f.SourceFeatureId).Should().BeEquivalentTo(
            "road.1", "road.2", "road.3", "road.1", "road.2", "road.3");

        // Idempotency: re-running the import converges to the same row set in the sink.
        second.FeaturesImported.Should().Be(3);
        second.Success.Should().BeTrue();
    }

    [Fact]
    public async Task ImportCollectionAsync_WithMaxFeaturesCap_StopsImportAndMarksTruncated()
    {
        var handler = new FixtureItemsHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://demo.example") };
        var sink = new RecordingSink();
        var service = CreateService(httpClient, sink);

        var result = await service.ImportCollectionAsync(NewRequest() with { MaxFeatures = 2 });

        result.Success.Should().BeTrue();
        result.FeaturesImported.Should().Be(2);
        result.Truncated.Should().BeTrue();
        result.Warnings.Should().Contain(warning => warning.Contains("feature limit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ImportCollectionAsync_WithNonJsonItems_ReturnsUnsupportedItemsEncoding()
    {
        var handler = new StubItemsHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<gml />", System.Text.Encoding.UTF8, "application/gml+xml")
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://demo.example") };
        var service = CreateService(httpClient, new RecordingSink());

        var result = await service.ImportCollectionAsync(NewRequest());

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(OgcApiFeaturesImportErrorCodes.InvalidItemsDocument);
    }

    [Fact]
    public async Task ImportCollectionAsync_WithJsonNonFeatureCollection_ReturnsUnsupportedItemsEncoding()
    {
        var handler = new StubItemsHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"hello\":\"world\"}", System.Text.Encoding.UTF8, "application/json")
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://demo.example") };
        var service = CreateService(httpClient, new RecordingSink());

        var result = await service.ImportCollectionAsync(NewRequest());

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(OgcApiFeaturesImportErrorCodes.UnsupportedItemsEncoding);
    }

    [Fact]
    public async Task ImportCollectionAsync_WithSinkFailure_ReturnsSinkFailureCode()
    {
        var handler = new FixtureItemsHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://demo.example") };
        var sink = new ThrowingSink();
        var service = CreateService(httpClient, sink);

        var result = await service.ImportCollectionAsync(NewRequest());

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(OgcApiFeaturesImportErrorCodes.SinkFailure);
    }

    [Fact]
    public async Task ImportCollectionAsync_WithInvalidServiceUrl_FailsBeforeContactingSource()
    {
        var handler = new FixtureItemsHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://demo.example") };
        var sink = new RecordingSink();
        var service = CreateService(httpClient, sink);

        var result = await service.ImportCollectionAsync(NewRequest() with { ServiceUrl = "not-a-url" });

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(OgcApiFeaturesImportErrorCodes.InvalidServiceUrl);
        sink.EnsureTargetCalls.Should().Be(0);
        handler.RequestUris.Should().BeEmpty();
    }

    [Fact]
    public async Task ImportCollectionAsync_WithUnreachableSource_ReturnsSourceUnreachable()
    {
        var handler = new StubItemsHandler(_ => throw new HttpRequestException("boom"));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://demo.example") };
        var sink = new RecordingSink();
        var service = CreateService(httpClient, sink);

        var result = await service.ImportCollectionAsync(NewRequest());

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(OgcApiFeaturesImportErrorCodes.SourceUnreachable);
    }

    [Fact]
    public async Task ImportCollectionAsync_WithInvalidIdentifierCharacters_FailsWithInvalidServiceUrlCode()
    {
        var handler = new FixtureItemsHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://demo.example") };
        var sink = new RecordingSink();
        var service = CreateService(httpClient, sink);

        var result = await service.ImportCollectionAsync(NewRequest() with { TargetSchema = "evil; DROP TABLE" });

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(OgcApiFeaturesImportErrorCodes.InvalidServiceUrl);
        sink.EnsureTargetCalls.Should().Be(0);
    }

    [Fact]
    public async Task ImportCollectionAsync_WithCyclicPagination_StopsAndWarns()
    {
        var handler = new CyclicItemsHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://demo.example") };
        var sink = new RecordingSink();
        var service = CreateService(httpClient, sink);

        var result = await service.ImportCollectionAsync(NewRequest());

        result.Success.Should().BeTrue();
        result.PagesFetched.Should().Be(1);
        result.Warnings.Should().Contain(warning => warning.Contains("paging cycle", StringComparison.Ordinal));
    }

    private static OgcApiFeaturesImportService CreateService(HttpClient httpClient, IOgcApiFeaturesCollectionSink sink)
        => new(
            httpClient,
            sink,
            NullLogger<OgcApiFeaturesImportService>.Instance,
            static (_, _) => Task.FromResult<IPAddress[]>([IPAddress.Parse("8.8.8.8")]));

    private static OgcApiFeaturesImportRequest NewRequest()
        => new()
        {
            ServiceUrl = "https://demo.example/ogcapi/",
            CollectionId = "roads",
            TargetSchema = "test_schema",
            PageSize = 2,
            TimeoutSeconds = 5
        };

    private sealed class RecordingSink : IOgcApiFeaturesCollectionSink
    {
        public int EnsureTargetCalls { get; private set; }

        public List<OgcApiFeaturesSinkFeature> WrittenFeatures { get; } = new();

        public Task EnsureTargetAsync(OgcApiFeaturesSinkTarget target, CancellationToken cancellationToken)
        {
            EnsureTargetCalls++;
            return Task.CompletedTask;
        }

        public Task<int> WriteFeaturesAsync(
            OgcApiFeaturesSinkTarget target,
            IReadOnlyList<OgcApiFeaturesSinkFeature> features,
            CancellationToken cancellationToken)
        {
            WrittenFeatures.AddRange(features);
            return Task.FromResult(features.Count);
        }
    }

    private sealed class ThrowingSink : IOgcApiFeaturesCollectionSink
    {
        public Task EnsureTargetAsync(OgcApiFeaturesSinkTarget target, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<int> WriteFeaturesAsync(
            OgcApiFeaturesSinkTarget target,
            IReadOnlyList<OgcApiFeaturesSinkFeature> features,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("sink offline");
    }

    private sealed class StubItemsHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _factory;

        public StubItemsHandler(Func<HttpRequestMessage, HttpResponseMessage> factory)
        {
            _factory = factory;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_factory(request));
    }

    private sealed class FixtureItemsHandler : HttpMessageHandler
    {
        public ConcurrentBag<Uri> RequestUris { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);
            var pathAndQuery = request.RequestUri!.PathAndQuery;

            string? body = pathAndQuery switch
            {
                "/ogcapi/collections/roads" => """
                    {
                      "id": "roads",
                      "title": "Roads",
                      "links": [
                        { "rel": "items", "href": "https://demo.example/ogcapi/collections/roads/items", "type": "application/geo+json" }
                      ]
                    }
                    """,
                "/ogcapi/collections/roads/items?limit=2" => """
                    {
                      "type": "FeatureCollection",
                      "numberMatched": 3,
                      "links": [
                        { "rel": "self", "href": "https://demo.example/ogcapi/collections/roads/items?limit=2", "type": "application/geo+json" },
                        { "rel": "next", "href": "https://demo.example/ogcapi/collections/roads/items?offset=2&limit=2", "type": "application/geo+json" }
                      ],
                      "features": [
                        {
                          "type": "Feature",
                          "id": "road.1",
                          "geometry": { "type": "Point", "coordinates": [-157.85, 21.30] },
                          "properties": { "name": "King" }
                        },
                        {
                          "type": "Feature",
                          "id": "road.2",
                          "geometry": { "type": "Point", "coordinates": [-157.86, 21.31] },
                          "properties": { "name": "Beretania" }
                        }
                      ]
                    }
                    """,
                "/ogcapi/collections/roads/items?offset=2&limit=2" => """
                    {
                      "type": "FeatureCollection",
                      "numberMatched": 3,
                      "links": [
                        { "rel": "self", "href": "https://demo.example/ogcapi/collections/roads/items?offset=2&limit=2", "type": "application/geo+json" }
                      ],
                      "features": [
                        {
                          "type": "Feature",
                          "id": "road.3",
                          "geometry": { "type": "Point", "coordinates": [-157.87, 21.32] },
                          "properties": { "name": "Hotel" }
                        }
                      ]
                    }
                    """,
                _ => null
            };

            return Task.FromResult(body == null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "application/geo+json")
                });
        }
    }

    private sealed class CyclicItemsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Every items page advertises a self-pointing next link, simulating a buggy upstream.
            const string body = """
                {
                  "type": "FeatureCollection",
                  "links": [
                    { "rel": "next", "href": "https://demo.example/ogcapi/collections/roads/items?limit=2" }
                  ],
                  "features": []
                }
                """;

            if (request.RequestUri!.PathAndQuery == "/ogcapi/collections/roads")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "id": "roads",
                          "links": [
                            { "rel": "items", "href": "https://demo.example/ogcapi/collections/roads/items", "type": "application/geo+json" }
                          ]
                        }
                        """,
                        System.Text.Encoding.UTF8,
                        "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/geo+json")
            });
        }
    }
}
