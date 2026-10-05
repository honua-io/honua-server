// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Protocols.OData;

/// <summary>
/// The request timeout must never let a streamed OData collection finish as a whole 200 response
/// once its headers are on the wire (#5472). A source that stalls after the first flush must make
/// the server abort the transport so the client sees the truncation, while a source that stalls
/// before any byte is sent still receives the shaped 408 response.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.ODataV4)]
public sealed class ODataStreamingTimeoutAbortTests : IAsyncLifetime
{
    // Above the handler's 32-row flush interval so the status line and first chunk are sent
    // before the source stalls.
    private const int RowsBeforeStall = 40;

    // The handler streams only when the requested $top exceeds its 1000-row streaming threshold;
    // smaller pages go through the buffered handler.
    private const string StreamingFeaturesPath = "/odata/Layers(0)/Features?$top=2000";

    // The options validator floors the configured timeout at 10 seconds; the test narrows the
    // middleware's value after validation so the stall resolves quickly.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private const int WarmupAttempts = 6;

    private readonly SlowSource _source = new();
    private readonly WebAppFixture _fixture;

    public ODataStreamingTimeoutAbortTests()
    {
        _fixture = new WebAppFixture()
            .UseKestrel()
            .DecorateService<IFeatureReader>(inner => new StallingFeatureReader(inner, _source))
            .ConfigureServices(services => services.AddSingleton<IOptions<LimitsOptions>>(provider =>
            {
                var limits = provider.GetRequiredService<IOptionsFactory<LimitsOptions>>().Create(Options.DefaultName);
                limits.Connections.RequestTimeout = RequestTimeout;
                return Options.Create(limits);
            }));
    }

    public async Task InitializeAsync()
    {
        _fixture.UseSeed(Path.Join("tests", "seed", "odata.yaml"));
        await _fixture.InitializeAsync();

        // Warm the streaming route through the real store so first-request startup cost cannot
        // spend the short timeout before the stream begins. A cold host can exceed the timeout on
        // its first requests, so retry until one whole page comes back.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var warmup = await _fixture.Client.GetAsync(StreamingFeaturesPath);
                if (warmup.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }

                warmup.StatusCode.Should().Be(HttpStatusCode.RequestTimeout, "only the short timeout may fail warm-up");
            }
            catch (HttpRequestException) when (attempt < WarmupAttempts)
            {
                // A warm-up page cut by the timeout after it started streaming; try again.
            }

            attempt.Should().BeLessThan(WarmupAttempts, "the streaming route must warm up within the timeout");
        }
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Layers({layerId})/Features")]
    public async Task Features_StreamedOverKestrel_CompletesWellFramedBody()
    {
        _fixture.Client.BaseAddress!.IsLoopback.Should().BeTrue(
            "only a real Kestrel host applies HTTP/1.1 chunked framing");
        _source.Mode = StallMode.None;

        using var response = await _fixture.Client.GetAsync(StreamingFeaturesPath);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TransferEncodingChunked.Should().BeTrue();
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("value").GetArrayLength().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Layers({layerId})/Features")]
    public async Task Features_TimeoutAfterStreamStarted_AbortsResponseInsteadOfCompletingTruncatedBody()
    {
        _fixture.Client.BaseAddress!.IsLoopback.Should().BeTrue(
            "only a real Kestrel host shows whether the chunked framing terminates abnormally");
        _source.Mode = StallMode.AfterFirstFlush;

        using var response = await _fixture.Client.GetAsync(
            StreamingFeaturesPath,
            HttpCompletionOption.ResponseHeadersRead);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the first flush sent the status line before the stall");

        string? completedBody = null;
        Exception? transportFailure = null;
        try
        {
            completedBody = await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            transportFailure = ex;
        }

        transportFailure.Should().NotBeNull(
            "an interrupted stream must surface as a transport failure, but the client read a complete " +
            "{0}-character 200 body ending in '{1}'",
            completedBody?.Length,
            completedBody?[^Math.Min(completedBody.Length, 40)..]);
        _source.Stalled.Should().BeTrue("the source must have reached its stall point");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Layers({layerId})/Features")]
    public async Task Features_TimeoutBeforeStreamStarted_ReturnsShapedRequestTimeout()
    {
        _source.Mode = StallMode.BeforeFirstRow;

        using var response = await _fixture.Client.GetAsync(StreamingFeaturesPath);

        response.StatusCode.Should().Be(HttpStatusCode.RequestTimeout);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().StartWith(
            "{\"error\"",
            "a timeout before any byte is sent returns only the shaped error, not a collection prefix");
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("RequestTimeout");
        _source.Stalled.Should().BeTrue("the source must have reached its stall point");
    }

    private enum StallMode
    {
        None,
        BeforeFirstRow,
        AfterFirstFlush,
    }

    private sealed class SlowSource
    {
        private int _stalled;

        public StallMode Mode { get; set; }

        public bool Stalled => Volatile.Read(ref _stalled) == 1;

        public async IAsyncEnumerable<Feature> StreamAsync(
            IAsyncEnumerable<Feature> inner,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (Mode == StallMode.None)
            {
                await foreach (var feature in inner.WithCancellation(cancellationToken))
                {
                    yield return feature;
                }

                yield break;
            }

            var rows = Mode == StallMode.AfterFirstFlush ? RowsBeforeStall : 0;
            for (var id = 1; id <= rows; id++)
            {
                yield return Feature.Create(
                    id,
                    geometry: null,
                    ImmutableDictionary<string, object?>.Empty.Add("name", $"row-{id}"));
            }

            Volatile.Write(ref _stalled, 1);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    /// <summary>
    /// The OData handler streams from the resolved reader whenever it is also a streaming store,
    /// so the stall is injected at the reader rather than at the separately registered
    /// <see cref="IStreamingFeatureStore"/>.
    /// </summary>
    private sealed class StallingFeatureReader(IFeatureReader inner, SlowSource source)
        : IFeatureReader, IStreamingFeatureStore
    {
        public IAsyncEnumerable<Feature> StreamFeaturesAsync(
            int layerId,
            FeatureQuery query,
            CancellationToken cancellationToken = default)
            => source.StreamAsync(InnerStream(layerId, query, cancellationToken), cancellationToken);

        public IAsyncEnumerable<IReadOnlyList<Feature>> StreamFeatureBatchesAsync(
            int layerId,
            FeatureQuery query,
            int batchSize = 1000,
            CancellationToken cancellationToken = default)
            => InnerStreamingStore.StreamFeatureBatchesAsync(layerId, query, batchSize, cancellationToken);

        public IAsyncEnumerable<GmlFeature> StreamGmlFeaturesAsync(
            int layerId,
            FeatureQuery query,
            CancellationToken cancellationToken = default)
            => InnerStreamingStore.StreamGmlFeaturesAsync(layerId, query, cancellationToken);

        public Task<Feature?> GetAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
            => inner.GetAsync(layerId, featureId, cancellationToken);

        public Task<QueryResult<Feature>> QueryAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
            => inner.QueryAsync(layerId, query, cancellationToken);

        public Task<byte[]?> QueryFlatGeobufAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
            => inner.QueryFlatGeobufAsync(layerId, query, cancellationToken);

        public Task<ImmutableArray<long>> QueryObjectIdsAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
            => inner.QueryObjectIdsAsync(layerId, query, cancellationToken);

        public Task<long> CountAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
            => inner.CountAsync(layerId, query, cancellationToken);

        public Task<FeatureExtent?> GetExtentAsync(int layerId, FeatureQuery? query = null, CancellationToken cancellationToken = default)
            => inner.GetExtentAsync(layerId, query, cancellationToken);

        public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryStatisticsAsync(
            int layerId,
            FeatureQuery query,
            CancellationToken cancellationToken = default)
            => inner.QueryStatisticsAsync(layerId, query, cancellationToken);

        public Task<TemporalExtentResult?> GetTemporalExtentAsync(
            int layerId,
            string fieldName,
            TemporalPropertyType propertyType,
            CancellationToken cancellationToken = default)
            => inner.GetTemporalExtentAsync(layerId, fieldName, propertyType, cancellationToken);

        public Task<EstimateResult> GetEstimatesAsync(int layerId, CancellationToken cancellationToken = default)
            => inner.GetEstimatesAsync(layerId, cancellationToken);

        public Task<QueryResult<Feature>> QueryTopFeaturesAsync(
            int layerId,
            FeatureQuery query,
            CancellationToken cancellationToken = default)
            => inner.QueryTopFeaturesAsync(layerId, query, cancellationToken);

        public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryDateBinsAsync(
            int layerId,
            FeatureQuery query,
            DateBinDefinition dateBin,
            CancellationToken cancellationToken = default)
            => inner.QueryDateBinsAsync(layerId, query, dateBin, cancellationToken);

        public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryBinsAsync(
            int layerId,
            FeatureQuery query,
            BinDefinition binDefinition,
            CancellationToken cancellationToken = default)
            => inner.QueryBinsAsync(layerId, query, binDefinition, cancellationToken);

        public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryH3Async(
            int layerId,
            FeatureQuery query,
            H3AggregationQuery h3Query,
            CancellationToken cancellationToken = default)
            => inner.QueryH3Async(layerId, query, h3Query, cancellationToken);

        private IStreamingFeatureStore InnerStreamingStore => inner as IStreamingFeatureStore
            ?? throw new InvalidOperationException("The decorated feature reader does not stream.");

        private async IAsyncEnumerable<Feature> InnerStream(
            int layerId,
            FeatureQuery query,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (inner is IStreamingFeatureStore streaming)
            {
                await foreach (var feature in streaming.StreamFeaturesAsync(layerId, query, cancellationToken))
                {
                    yield return feature;
                }

                yield break;
            }

            var result = await inner.QueryAsync(layerId, query, cancellationToken);
            foreach (var feature in result.Items)
            {
                yield return feature;
            }
        }
    }
}
