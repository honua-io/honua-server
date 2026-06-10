// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.SpatialAnalytics.Abstractions;
using Honua.Core.Features.SpatialAnalytics.Domain;
using Honua.Server.Features.Protocols.SpatialAnalytics;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.SpatialAnalytics;

/// <summary>
/// Unit tests for <see cref="SpatialAnalyticsRequestHandlers.TryGetAnalyticsReader"/>,
/// the gate that converts a missing <see cref="ISpatialAnalyticsReader"/> registration
/// into a contract-compliant HTTP 501 instead of leaking an <c>InvalidOperationException</c>
/// through to HTTP 500. This matters on the DuckDB read-only provider which maps the
/// analytics routes alongside the rest of FeatureServer but does not ship a backend.
/// </summary>
[Protocol(TestProtocols.SpatialAnalytics)]
public sealed class SpatialAnalyticsReaderAvailabilityTests
{
    [UnitTest]
    [Protocol(TestProtocols.SpatialAnalytics)]
    public async Task TryGetAnalyticsReader_NoReaderRegistered_ReturnsNotImplemented()
    {
        var context = BuildHttpContext(registerReader: false);

        var resolved = SpatialAnalyticsRequestHandlers.TryGetAnalyticsReader(
            context, "queryClusters", NullLogger.Instance, out var reader, out var errorResult);

        resolved.Should().BeFalse();
        reader.Should().BeNull();
        errorResult.Should().NotBeNull();
        await AssertStatusCodeAsync(errorResult!, StatusCodes.Status501NotImplemented);
    }

    [UnitTest]
    [Protocol(TestProtocols.SpatialAnalytics)]
    public void TryGetAnalyticsReader_ReaderRegistered_ReturnsReader()
    {
        var context = BuildHttpContext(registerReader: true);

        var resolved = SpatialAnalyticsRequestHandlers.TryGetAnalyticsReader(
            context, "spatialJoin", NullLogger.Instance, out var reader, out var errorResult);

        resolved.Should().BeTrue();
        reader.Should().NotBeNull();
        errorResult.Should().BeNull();
    }

    [UnitTest]
    [Protocol(TestProtocols.SpatialAnalytics)]
    public async Task TryGetAnalyticsReader_NullLoggerMissingReader_StillReturnsNotImplemented()
    {
        // The helper tolerates a null logger (it's resolved best-effort from DI)
        // so missing observability must not change the 501 contract.
        var context = BuildHttpContext(registerReader: false);

        var resolved = SpatialAnalyticsRequestHandlers.TryGetAnalyticsReader(
            context, "queryBufferAggregate", logger: null, out var reader, out var errorResult);

        resolved.Should().BeFalse();
        reader.Should().BeNull();
        await AssertStatusCodeAsync(errorResult!, StatusCodes.Status501NotImplemented);
    }

    private static DefaultHttpContext BuildHttpContext(bool registerReader)
    {
        var services = new ServiceCollection();
        if (registerReader)
        {
            services.AddSingleton<ISpatialAnalyticsReader, StubSpatialAnalyticsReader>();
        }

        return new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider()
        };
    }

    private static async Task AssertStatusCodeAsync(IResult result, int expected)
    {
        if (result is IStatusCodeHttpResult statusCodeResult)
        {
            statusCodeResult.StatusCode.Should().Be(expected);
            return;
        }

        var ctx = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        await result.ExecuteAsync(ctx);
        ctx.Response.StatusCode.Should().Be(expected);
    }

    private sealed class StubSpatialAnalyticsReader : ISpatialAnalyticsReader
    {
        public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryClustersAsync(
            int layerId, FeatureQuery query, ClusterQuery clusterQuery, CancellationToken cancellationToken = default) =>
            Task.FromResult(ImmutableArray<IReadOnlyDictionary<string, object?>>.Empty);

        public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryBufferAggregateAsync(
            int layerId, FeatureQuery query, BufferAggregateQuery bufferQuery, CancellationToken cancellationToken = default) =>
            Task.FromResult(ImmutableArray<IReadOnlyDictionary<string, object?>>.Empty);

        public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryDensityAsync(
            int layerId, FeatureQuery query, DensityQuery densityQuery, CancellationToken cancellationToken = default) =>
            Task.FromResult(ImmutableArray<IReadOnlyDictionary<string, object?>>.Empty);

        public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QuerySpatialJoinAsync(
            int targetLayerId, FeatureQuery targetQuery, SpatialJoinQuery joinQuery, CancellationToken cancellationToken = default) =>
            Task.FromResult(ImmutableArray<IReadOnlyDictionary<string, object?>>.Empty);
    }
}
