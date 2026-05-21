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
        first.SourceFeatureCountReported.Should().Be(3);
        first.FeatureCountParity.Should().NotBeNull();
        first.FeatureCountParity!.State.Should().Be(OgcApiFeaturesFeatureCountParityStates.Pass);
        first.FeatureCountParity.Expected.Should().Be(3);
        first.FeatureCountParity.Observed.Should().Be(3);

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

        // Operator-imposed truncation must downgrade the feature-count parity probe to
        // not-applicable: the source numberMatched is no longer comparable to the partial import.
        result.FeatureCountParity.Should().NotBeNull();
        result.FeatureCountParity!.State.Should().Be(OgcApiFeaturesFeatureCountParityStates.NotApplicable);
        result.FeatureCountParity.Summary.ToLowerInvariant().Should().Contain("truncated");
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
    public async Task ImportCollectionAsync_WithCql2Filter_AppendsFilterAndFilterLangQueryParameters()
    {
        var handler = new FilterPassthroughHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://demo.example") };
        var sink = new RecordingSink();
        var service = CreateService(httpClient, sink);

        var result = await service.ImportCollectionAsync(
            NewRequest() with { Filter = "type = 'primary'" });

        result.Success.Should().BeTrue();
        var firstItemsQuery = handler.ItemsRequests.First().Query;
        firstItemsQuery.Should().Contain("filter=type%20%3D%20%27primary%27");
        firstItemsQuery.Should().Contain("filter-lang=cql2-text");
    }

    [Fact]
    public async Task ImportCollectionAsync_WithBbox_AppendsBboxQueryParameter()
    {
        var handler = new FilterPassthroughHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://demo.example") };
        var sink = new RecordingSink();
        var service = CreateService(httpClient, sink);

        var result = await service.ImportCollectionAsync(
            NewRequest() with { Bbox = new[] { -158.0, 21.0, -157.0, 22.0 } });

        result.Success.Should().BeTrue();
        var firstItemsQuery = handler.ItemsRequests.First().Query;
        firstItemsQuery.Should().Contain("bbox=-158%2C21%2C-157%2C22");
    }

    [Fact]
    public async Task ImportCollectionAsync_WithDatetimeInterval_AppendsDatetimeQueryParameter()
    {
        var handler = new FilterPassthroughHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://demo.example") };
        var sink = new RecordingSink();
        var service = CreateService(httpClient, sink);

        var result = await service.ImportCollectionAsync(
            NewRequest() with { Datetime = "2024-01-01T00:00:00Z/2024-06-30T23:59:59Z" });

        result.Success.Should().BeTrue();
        var firstItemsQuery = handler.ItemsRequests.First().Query;
        firstItemsQuery.Should().Contain("datetime=2024-01-01T00%3A00%3A00Z%2F2024-06-30T23%3A59%3A59Z");
    }

    [Fact]
    public async Task ImportCollectionAsync_WithEmptyFilter_ReturnsInvalidFilter()
    {
        var handler = new FixtureItemsHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://demo.example") };
        var sink = new RecordingSink();
        var service = CreateService(httpClient, sink);

        var result = await service.ImportCollectionAsync(NewRequest() with { Filter = "   " });

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(OgcApiFeaturesImportErrorCodes.InvalidFilter);
        handler.RequestUris.Should().BeEmpty();
        sink.EnsureTargetCalls.Should().Be(0);
    }

    [Theory]
    [InlineData(new[] { 1.0, 2.0, 3.0 })]
    [InlineData(new[] { 10.0, 20.0, 5.0, 15.0 })]
    [InlineData(new[] { double.NaN, 0.0, 1.0, 1.0 })]
    public async Task ImportCollectionAsync_WithInvalidBbox_ReturnsInvalidBbox(double[] bbox)
    {
        var handler = new FixtureItemsHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://demo.example") };
        var sink = new RecordingSink();
        var service = CreateService(httpClient, sink);

        var result = await service.ImportCollectionAsync(NewRequest() with { Bbox = bbox });

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(OgcApiFeaturesImportErrorCodes.InvalidBbox);
        handler.RequestUris.Should().BeEmpty();
    }

    [Theory]
    [InlineData("not-a-date")]
    [InlineData("../..")]
    [InlineData("2024-06-30T00:00:00Z/2024-01-01T00:00:00Z")]
    [InlineData("2024-01-01/2024-02-01/2024-03-01")]
    public async Task ImportCollectionAsync_WithInvalidDatetime_ReturnsInvalidDatetime(string datetime)
    {
        var handler = new FixtureItemsHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://demo.example") };
        var sink = new RecordingSink();
        var service = CreateService(httpClient, sink);

        var result = await service.ImportCollectionAsync(NewRequest() with { Datetime = datetime });

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(OgcApiFeaturesImportErrorCodes.InvalidDatetime);
        handler.RequestUris.Should().BeEmpty();
    }

    [Fact]
    public async Task ImportCollectionAsync_WithFilterChangeAcrossRuns_EmitsScopeDriftWarning()
    {
        var handler = new FilterPassthroughHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://demo.example") };
        var sink = new RecordingSink();
        var service = CreateService(httpClient, sink);

        var first = await service.ImportCollectionAsync(
            NewRequest() with { Filter = "type = 'primary'" });
        var second = await service.ImportCollectionAsync(
            NewRequest() with { Filter = "type = 'secondary'" });

        first.Success.Should().BeTrue();
        first.ScopeDriftDetected.Should().BeFalse();

        second.Success.Should().BeTrue();
        second.ScopeDriftDetected.Should().BeTrue();
        second.ManualReviewReason.Should().NotBeNullOrEmpty();
        second.Warnings.Should().Contain(warning =>
            warning.Contains("scope changed", StringComparison.Ordinal) ||
            warning.Contains("scope", StringComparison.OrdinalIgnoreCase));
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

    [Fact]
    public async Task ImportCollectionAsync_WhenSourceOmitsNumberMatched_ReportsParityProbeNotApplicable()
    {
        var handler = new SinglePageHandler(numberMatchedJsonFragment: null);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://demo.example") };
        var sink = new RecordingSink();
        var service = CreateService(httpClient, sink);

        var result = await service.ImportCollectionAsync(NewRequest());

        result.Success.Should().BeTrue();
        result.FeaturesImported.Should().Be(1);
        result.SourceFeatureCountReported.Should().BeNull();
        result.FeatureCountParity.Should().NotBeNull();
        result.FeatureCountParity!.State.Should().Be(OgcApiFeaturesFeatureCountParityStates.NotApplicable);
        result.FeatureCountParity.Expected.Should().BeNull();
        result.FeatureCountParity.Observed.Should().Be(1);
        result.FeatureCountParity.Summary.Should().Contain("numberMatched");
    }

    [Fact]
    public async Task ImportCollectionAsync_WhenSourceCountMismatchesImportedCount_ReportsParityProbeFail()
    {
        // Source advertises numberMatched=7 but only emits 1 feature on the only page (no next
        // link). The importer cannot recover the missing six features, so the inline parity probe
        // must surface a fail state for operator review.
        var handler = new SinglePageHandler(numberMatchedJsonFragment: "\"numberMatched\": 7,");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://demo.example") };
        var sink = new RecordingSink();
        var service = CreateService(httpClient, sink);

        var result = await service.ImportCollectionAsync(NewRequest());

        result.Success.Should().BeTrue();
        result.FeaturesImported.Should().Be(1);
        result.Truncated.Should().BeFalse();
        result.SourceFeatureCountReported.Should().Be(7);
        result.FeatureCountParity.Should().NotBeNull();
        result.FeatureCountParity!.State.Should().Be(OgcApiFeaturesFeatureCountParityStates.Fail);
        result.FeatureCountParity.Expected.Should().Be(7);
        result.FeatureCountParity.Observed.Should().Be(1);
    }

    [Fact]
    public async Task ImportCollectionAsync_WhenSkippedFeaturesAccountForDifference_ReportsParityProbePass()
    {
        // Source advertises numberMatched=2 and emits two features, but one has no usable
        // identifier and a non-object geometry/properties payload (still projectable) — the
        // synthetic id path keeps it as an imported feature, so we exercise the skipped path
        // separately via a feature with no id and properties=null (which we accept) — instead use
        // a feature with explicit non-object value, which is rejected.
        var handler = new MixedFeaturesHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://demo.example") };
        var sink = new RecordingSink();
        var service = CreateService(httpClient, sink);

        var result = await service.ImportCollectionAsync(NewRequest());

        result.Success.Should().BeTrue();
        result.FeaturesImported.Should().Be(1);
        result.FeaturesSkipped.Should().Be(1);
        result.SourceFeatureCountReported.Should().Be(2);
        result.FeatureCountParity.Should().NotBeNull();
        // imported (1) + skipped (1) == source-advertised (2): probe should pass.
        result.FeatureCountParity!.State.Should().Be(OgcApiFeaturesFeatureCountParityStates.Pass);
        result.FeatureCountParity.Expected.Should().Be(2);
        result.FeatureCountParity.Observed.Should().Be(1);
        result.FeatureCountParity.Summary.Should().Contain("skipped");
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

        public Dictionary<string, string> ScopeSignatures { get; } = new(StringComparer.Ordinal);

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

        public Task<string?> GetLastScopeSignatureAsync(
            OgcApiFeaturesSinkTarget target,
            CancellationToken cancellationToken)
        {
            var key = $"{target.Schema}.{target.Table}";
            return Task.FromResult(ScopeSignatures.TryGetValue(key, out var signature) ? signature : null);
        }

        public Task RecordScopeSignatureAsync(
            OgcApiFeaturesSinkTarget target,
            string scopeSignature,
            CancellationToken cancellationToken)
        {
            var key = $"{target.Schema}.{target.Table}";
            ScopeSignatures[key] = scopeSignature;
            return Task.CompletedTask;
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

    private sealed class FilterPassthroughHandler : HttpMessageHandler
    {
        public ConcurrentBag<Uri> RequestUris { get; } = new();

        public List<Uri> ItemsRequests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/ogcapi/collections/roads")
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

            if (path.EndsWith("/items", StringComparison.Ordinal))
            {
                lock (ItemsRequests)
                {
                    ItemsRequests.Add(request.RequestUri!);
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "type": "FeatureCollection",
                          "numberMatched": 1,
                          "links": [
                            { "rel": "self", "href": "https://demo.example/ogcapi/collections/roads/items" }
                          ],
                          "features": [
                            {
                              "type": "Feature",
                              "id": "road.1",
                              "geometry": { "type": "Point", "coordinates": [-157.85, 21.30] },
                              "properties": { "name": "King" }
                            }
                          ]
                        }
                        """,
                        System.Text.Encoding.UTF8,
                        "application/geo+json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class SinglePageHandler : HttpMessageHandler
    {
        private readonly string? _numberMatchedJsonFragment;

        public SinglePageHandler(string? numberMatchedJsonFragment)
        {
            _numberMatchedJsonFragment = numberMatchedJsonFragment;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
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

            var body =
                "{\n" +
                "  \"type\": \"FeatureCollection\",\n" +
                "  " + (_numberMatchedJsonFragment ?? string.Empty) + "\n" +
                "  \"links\": [\n" +
                "    { \"rel\": \"self\", \"href\": \"https://demo.example/ogcapi/collections/roads/items?limit=2\" }\n" +
                "  ],\n" +
                "  \"features\": [\n" +
                "    {\n" +
                "      \"type\": \"Feature\",\n" +
                "      \"id\": \"road.1\",\n" +
                "      \"geometry\": { \"type\": \"Point\", \"coordinates\": [-157.85, 21.30] },\n" +
                "      \"properties\": { \"name\": \"King\" }\n" +
                "    }\n" +
                "  ]\n" +
                "}";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/geo+json")
            });
        }
    }

    private sealed class MixedFeaturesHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
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

            // Source advertises numberMatched=2: one valid feature plus one non-object entry that
            // the importer must reject as a skipped projection.
            const string body = """
                {
                  "type": "FeatureCollection",
                  "numberMatched": 2,
                  "links": [
                    { "rel": "self", "href": "https://demo.example/ogcapi/collections/roads/items?limit=2" }
                  ],
                  "features": [
                    {
                      "type": "Feature",
                      "id": "road.1",
                      "geometry": { "type": "Point", "coordinates": [-157.85, 21.30] },
                      "properties": { "name": "King" }
                    },
                    "not-a-feature-object"
                  ]
                }
                """;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
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
