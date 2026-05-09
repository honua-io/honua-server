// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerQueryH3Tests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.QueryH3)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryH3")]
    public async Task QueryH3_ValidResolution_ReturnsOkOrCapabilityError()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryH3?resolution=7&f=json");

        // The test database may not have h3-pg installed, so accept OK, 501 (missing), or 503 (transient)
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotImplemented, HttpStatusCode.ServiceUnavailable);

        var content = await response.Content.ReadAsStringAsync();
        if (response.StatusCode == HttpStatusCode.OK)
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            root.TryGetProperty("features", out var features).Should().BeTrue();
            features.ValueKind.Should().Be(JsonValueKind.Array);
        }
        else
        {
            // Capability error should mention h3-pg
            content.Should().Contain("h3-pg");
        }
    }

    [IntegrationTest]
    [Operation(Operations.QueryH3)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryH3")]
    public async Task QueryH3_MissingResolution_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryH3?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("resolution");
    }

    [IntegrationTest]
    [Operation(Operations.QueryH3)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryH3")]
    public async Task QueryH3_InvalidResolution_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryH3?resolution=20&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("resolution");
    }

    [IntegrationTest]
    [Operation(Operations.QueryH3)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryH3")]
    public async Task QueryH3_InvalidService_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            "/rest/services/nonexistent/FeatureServer/0/queryH3?resolution=7&f=json");

        // Resolution is valid so validation passes; service lookup fails before h3 capability check
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.QueryH3)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryH3")]
    public async Task QueryH3Post_ValidRequest_ReturnsOkOrCapabilityError()
    {
        var payload = JsonSerializer.Serialize(new
        {
            resolution = 7,
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryH3",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        // Accept OK (h3-pg available), 501 (missing), or 503 (transient)
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotImplemented, HttpStatusCode.ServiceUnavailable);

        var content = await response.Content.ReadAsStringAsync();
        if (response.StatusCode == HttpStatusCode.OK)
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            root.TryGetProperty("features", out var features).Should().BeTrue();
            features.ValueKind.Should().Be(JsonValueKind.Array);
        }
    }

    [IntegrationTest]
    [Operation(Operations.QueryH3)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryH3")]
    public async Task QueryH3_NegativeResolution_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryH3?resolution=-1&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.QueryH3)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryH3")]
    public async Task QueryH3_WithKRingDistance_ReturnsOkOrCapabilityError()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryH3?resolution=7&kRingDistance=1&f=json");

        // kRingDistance=1 is valid; outcome depends on h3-pg availability
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotImplemented, HttpStatusCode.ServiceUnavailable);
    }

    [IntegrationTest]
    [Operation(Operations.QueryH3)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryH3")]
    public async Task QueryH3_KRingDistanceZero_ReturnsOkOrCapabilityError()
    {
        // kRingDistance=0 is valid and should take the non-kRing code path
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryH3?resolution=7&kRingDistance=0&f=json");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotImplemented, HttpStatusCode.ServiceUnavailable);
    }

    [IntegrationTest]
    [Operation(Operations.QueryH3)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryH3")]
    public async Task QueryH3_ExcessiveKRingDistance_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryH3?resolution=7&kRingDistance=99&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("kRingDistance");
    }

    [IntegrationTest]
    [Operation(Operations.QueryH3)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryH3")]
    public async Task QueryH3Post_WithOutStatisticsArray_ReturnsOkOrCapabilityError()
    {
        // Verify that a POST body with a native JSON array for outStatistics is accepted
        // (regression test: TryReadRequestValuesAsync flattens JSON arrays into StringValues)
        var payload = JsonSerializer.Serialize(new
        {
            resolution = 7,
            outStatistics = new[]
            {
                new { statisticType = "count", onStatisticField = "objectid", outStatisticFieldName = "cnt" }
            },
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryH3",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        // Accept OK (h3-pg available), 501 (missing), or 503 (transient) — but NOT 400
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotImplemented, HttpStatusCode.ServiceUnavailable);
    }

    [IntegrationTest]
    [Operation(Operations.QueryH3)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryH3")]
    public async Task QueryH3Post_WithSpatialAggregationSummaries_ReturnsSdkSummaryResponse()
    {
        var fixture = new WebAppFixture()
            .ReplaceService<IH3CapabilityChecker>(new AlwaysAvailableH3Checker())
            .ReplaceService<IFeatureReader>(new SpatialAggregationSummaryFeatureReader());
        await fixture.InitializeAsync();
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                requestId = "summary-smoke",
                resolution = new { indexResolution = 7, model = "h3" },
                summaries = new object[]
                {
                    new { id = "featureCount", kind = "count", title = "Features" },
                    new { id = "byCategory", kind = "category", field = "category", limit = 5 },
                    new { id = "valueHistogram", kind = "histogram", field = "value", bins = 2, min = 0, max = 100 },
                    new
                    {
                        id = "valueRanges",
                        kind = "range",
                        field = "value",
                        ranges = new[]
                        {
                            new { id = "low", label = "Low", min = 0, max = 50, includeMin = true, includeMax = false },
                            new { id = "high", label = "High", min = 50, max = 100, includeMin = true, includeMax = true }
                        }
                    }
                },
                groupBy = new[] { new { field = "category", alias = "category" } },
                include = new { cells = true, totals = true },
                page = new { limit = 10 },
                f = "json"
            });

            var response = await fixture.Client.PostAsync(
                $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryH3",
                new StringContent(payload, Encoding.UTF8, "application/json"));

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;
            root.GetProperty("schemaVersion").GetString().Should().Be("honua.spatial-aggregation.v1");
            root.GetProperty("requestId").GetString().Should().Be("summary-smoke");
            root.GetProperty("index").GetProperty("model").GetProperty("id").GetString().Should().Be("h3");
            root.GetProperty("metadata").GetProperty("widgets").GetArrayLength().Should().BeGreaterThan(0);
            root.GetProperty("totals").TryGetProperty("featureCount", out _).Should().BeTrue();
            root.GetProperty("totals").TryGetProperty("byCategory", out _).Should().BeTrue();
            root.GetProperty("totals").TryGetProperty("valueHistogram", out _).Should().BeTrue();
            root.GetProperty("totals").TryGetProperty("valueRanges", out _).Should().BeTrue();

            var firstCell = root.GetProperty("cells").EnumerateArray().Should().ContainSingle().Subject;
            var cellSummaries = firstCell.GetProperty("summaries");
            cellSummaries.GetProperty("featureCount").GetProperty("value").GetInt64().Should().Be(3);
            cellSummaries.GetProperty("byCategory").GetProperty("buckets").GetArrayLength().Should().BeGreaterThan(0);
            cellSummaries.GetProperty("valueHistogram").GetProperty("buckets").GetArrayLength().Should().Be(2);
            cellSummaries.GetProperty("valueRanges").GetProperty("buckets").GetArrayLength().Should().Be(2);
            root.GetProperty("groups").GetArrayLength().Should().BeGreaterThan(0);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.QueryH3)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryH3")]
    public async Task QueryH3Post_WithHistogramSummariesAndNoTotals_ResolvesCellBounds()
    {
        var fixture = new WebAppFixture()
            .ReplaceService<IH3CapabilityChecker>(new AlwaysAvailableH3Checker())
            .ReplaceService<IFeatureReader>(new SpatialAggregationSummaryFeatureReader());
        await fixture.InitializeAsync();
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                resolution = 7,
                summaries = new object[]
                {
                    new { id = "valueHistogram", kind = "histogram", field = "value", bins = 2 }
                },
                include = new { cells = true, totals = false },
                f = "json"
            });

            var response = await fixture.Client.PostAsync(
                $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryH3",
                new StringContent(payload, Encoding.UTF8, "application/json"));

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;
            root.TryGetProperty("totals", out _).Should().BeFalse();
            var firstCell = root.GetProperty("cells").EnumerateArray().Should().ContainSingle().Subject;
            firstCell.GetProperty("summaries").GetProperty("valueHistogram").GetProperty("buckets").GetArrayLength().Should().Be(2);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.QueryH3)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryH3")]
    public async Task QueryH3Post_WithLargeCountSummary_PreservesIntegerPrecision()
    {
        const long preciseCount = 9_007_199_254_740_993L;
        var fixture = new WebAppFixture()
            .ReplaceService<IH3CapabilityChecker>(new AlwaysAvailableH3Checker())
            .ReplaceService<IFeatureReader>(new SpatialAggregationSummaryFeatureReader(preciseCount, preciseCount));
        await fixture.InitializeAsync();
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                resolution = 7,
                summaries = new object[]
                {
                    new { id = "featureCount", kind = "count", title = "Features" }
                },
                include = new { cells = true, totals = true },
                f = "json"
            });

            var response = await fixture.Client.PostAsync(
                $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryH3",
                new StringContent(payload, Encoding.UTF8, "application/json"));

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var content = await response.Content.ReadAsStringAsync();
            var preciseCountJson = preciseCount.ToString(CultureInfo.InvariantCulture);
            content.Should().Contain($"\"value\":{preciseCountJson}");
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            var totalValue = root.GetProperty("totals").GetProperty("featureCount").GetProperty("value");
            totalValue.GetRawText().Should().Be(preciseCountJson);
            totalValue.GetInt64().Should().Be(preciseCount);
            var firstCell = root.GetProperty("cells").EnumerateArray().Should().ContainSingle().Subject;
            var cellValue = firstCell.GetProperty("summaries").GetProperty("featureCount").GetProperty("value");
            cellValue.GetRawText().Should().Be(preciseCountJson);
            cellValue.GetInt64().Should().Be(preciseCount);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private sealed class AlwaysAvailableH3Checker : IH3CapabilityChecker
    {
        public Task<bool?> IsH3AvailableAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<bool?>(true);
    }

    private sealed class SpatialAggregationSummaryFeatureReader : IFeatureReader
    {
        private readonly long _totalCount;
        private readonly long _cellCount;

        public SpatialAggregationSummaryFeatureReader(long totalCount = 5L, long cellCount = 3L)
        {
            _totalCount = totalCount;
            _cellCount = cellCount;
        }

        public Task<Feature?> GetAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<QueryResult<Feature>> QueryAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<byte[]?> QueryFlatGeobufAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ImmutableArray<long>> QueryObjectIdsAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<long> CountAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult(4L);

        public Task<FeatureExtent?> GetExtentAsync(int layerId, FeatureQuery? query = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryStatisticsAsync(
            int layerId,
            FeatureQuery query,
            CancellationToken cancellationToken = default)
        {
            if (query.GroupByFields is { IsDefaultOrEmpty: false } groupByFields)
            {
                var countAlias = query.OutStatistics?.FirstOrDefault().OutStatisticFieldName ?? "count";
                var grouped = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    [groupByFields[0]] = "test",
                    [countAlias] = 2L
                };
                return Task.FromResult(ImmutableArray.Create<IReadOnlyDictionary<string, object?>>(grouped));
            }

            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var statistic in query.OutStatistics ?? ImmutableArray<StatisticDefinition>.Empty)
            {
                row[statistic.OutStatisticFieldName] = statistic.StatisticType switch
                {
                    StatisticType.Count => (object)_totalCount,
                    StatisticType.Sum => 42d,
                    StatisticType.Avg => 8.4d,
                    StatisticType.Min => 0d,
                    StatisticType.Max => 100d,
                    _ => 0d
                };
            }

            return Task.FromResult(ImmutableArray.Create<IReadOnlyDictionary<string, object?>>(row));
        }

        public Task<TemporalExtentResult?> GetTemporalExtentAsync(
            int layerId,
            string fieldName,
            FieldType fieldType,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<EstimateResult> GetEstimatesAsync(int layerId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<QueryResult<Feature>> QueryTopFeaturesAsync(
            int layerId,
            FeatureQuery query,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryDateBinsAsync(
            int layerId,
            FeatureQuery query,
            DateBinDefinition dateBin,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryBinsAsync(
            int layerId,
            FeatureQuery query,
            BinDefinition binDefinition,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryH3Async(
            int layerId,
            FeatureQuery query,
            H3AggregationQuery h3Query,
            CancellationToken cancellationToken = default)
        {
            foreach (var summary in h3Query.SummaryDefinitions.GetValueOrDefault())
            {
                if (summary.Kind == SpatialAggregationSummaryKind.Histogram &&
                    (!summary.HistogramMin.HasValue || !summary.HistogramMax.HasValue))
                {
                    throw new InvalidOperationException("Histogram cell summaries must have finite bounds before query execution.");
                }
            }

            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["cellIndex"] = "8928308280fffff",
                ["cellGeometry"] = "{\"type\":\"Polygon\",\"coordinates\":[[[-122.5,37.5],[-122.4,37.5],[-122.4,37.6],[-122.5,37.6],[-122.5,37.5]]]}",
                ["featureCount"] = _cellCount,
                ["byCategory"] = "{\"kind\":\"category\",\"buckets\":[{\"value\":\"test\",\"count\":3}],\"otherCount\":0,\"nullCount\":0}",
                ["valueHistogram"] = "{\"kind\":\"histogram\",\"buckets\":[{\"min\":0,\"max\":50,\"count\":1,\"includeMin\":true,\"includeMax\":false},{\"min\":50,\"max\":100,\"count\":2,\"includeMin\":true,\"includeMax\":true}]}",
                ["valueRanges"] = "{\"kind\":\"range\",\"buckets\":[{\"id\":\"low\",\"label\":\"Low\",\"min\":0,\"max\":50,\"count\":1,\"includeMin\":true,\"includeMax\":false},{\"id\":\"high\",\"label\":\"High\",\"min\":50,\"max\":100,\"count\":2,\"includeMin\":true,\"includeMax\":true}]}"
            };
            return Task.FromResult(ImmutableArray.Create<IReadOnlyDictionary<string, object?>>(row));
        }
    }
}
