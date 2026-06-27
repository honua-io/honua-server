// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Honua.Core.Features.Federation;
using Honua.Core.Features.Federation.Abstractions;
using Honua.Core.Features.Federation.Domain;
using Honua.Core.Features.Federation.Services;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Resilience;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Honua.Core.Tests.Features.Federation;

/// <summary>
/// Unit tests for the remote federated-query executor (issue #341). Remote I/O is provided by
/// in-memory connectors, so these tests exercise the real executor behaviour — push-down vs
/// local refinement, timeout, circuit breaker, and multi-source combination — without a live
/// remote source.
/// </summary>
public sealed class FederatedQueryExecutorTests
{
    private static readonly ResiliencePolicyOptions FastFail = new()
    {
        MaxRetryAttempts = 0,
        CircuitBreakerFailures = 2,
        CircuitBreakDuration = TimeSpan.FromMinutes(5),
    };

    private static FederatedSourceDescriptor Source(
        FederatedSourceKind kind,
        TimeSpan? timeout = null) => new()
        {
            Id = $"src-{kind}",
            DisplayName = kind.ToString(),
            Kind = kind,
            Endpoint = new Uri("https://remote.example.com/"),
            RemoteLayer = "0",
            RequestTimeout = timeout ?? TimeSpan.FromSeconds(30),
        };

    private static Feature Feature(long id, params (string Key, object? Value)[] attributes)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, object?>();
        foreach (var (key, value) in attributes)
        {
            builder[key] = value;
        }

        return new Feature { Id = id, Geometry = null, Attributes = builder.ToImmutable() };
    }

    private static FederatedQueryExecutor BuildExecutor(
        IFederatedSourceConnector connector,
        ResiliencePolicyOptions? options = null)
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddFederationCore();
        var planner = services.BuildServiceProvider().GetRequiredService<IFederationQueryPlanner>();

        return new FederatedQueryExecutor(
            planner,
            new[] { connector },
            options ?? ResiliencePolicyOptions.Default,
            NullLogger<FederatedQueryExecutor>.Instance);
    }

    [UnitTest]
    public async Task ExecuteAsync_NoConnector_ThrowsNoConnector()
    {
        var executor = BuildExecutor(new StubConnector(FederatedSourceKind.HonuaGrpc, ImmutableArray<Feature>.Empty));

        var ex = await Assert.ThrowsAsync<FederatedSourceUnavailableException>(
            () => executor.ExecuteAsync(Source(FederatedSourceKind.EsriRest), new FeatureQuery()));

        Assert.Equal(FederatedSourceUnavailableReason.NoConnector, ex.Reason);
    }

    [UnitTest]
    public async Task ExecuteAsync_PushDownOnly_ReturnsCandidatesWithoutLocalRefinement()
    {
        var rows = ImmutableArray.Create(Feature(1, ("name", "b")), Feature(2, ("name", "a")));
        var executor = BuildExecutor(new StubConnector(FederatedSourceKind.HonuaGrpc, rows));

        // HonuaGrpc pushes ordering down, so the executor must not re-sort locally.
        var query = new FeatureQuery { OrderBy = ImmutableArray.Create(OrderByClause.Asc("name")) };
        var result = await executor.ExecuteAsync(Source(FederatedSourceKind.HonuaGrpc), query);

        Assert.Empty(result.UnappliedLocalRefinements);
        Assert.Equal(new long[] { 1, 2 }, result.Result.Items.Select(f => f.Id).ToArray());
    }

    [UnitTest]
    public async Task ExecuteAsync_LocalOrderByAndPaging_SortsThenPages()
    {
        var rows = ImmutableArray.Create(
            Feature(1, ("name", "charlie")),
            Feature(2, ("name", "alpha")),
            Feature(3, ("name", "bravo")));
        var executor = BuildExecutor(new StubConnector(FederatedSourceKind.OgcWfs, rows));

        // OGC WFS does not push ordering down, which also forces paging to be applied locally.
        var query = new FeatureQuery
        {
            OrderBy = ImmutableArray.Create(OrderByClause.Asc("name")),
            Offset = 1,
            Limit = 1,
        };

        var result = await executor.ExecuteAsync(Source(FederatedSourceKind.OgcWfs), query);

        // Sorted -> alpha(2), bravo(3), charlie(1); offset 1, limit 1 -> bravo(3).
        Assert.Equal(new long[] { 3 }, result.Result.Items.Select(f => f.Id).ToArray());
        Assert.Equal(3, result.Result.TotalCount);
        Assert.True(result.Result.HasMoreResults);
    }

    [UnitTest]
    public async Task ExecuteAsync_LocalTemporalFilter_KeepsOnlyOverlappingRows()
    {
        var rows = ImmutableArray.Create(
            Feature(1, ("t", new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero))),
            Feature(2, ("t", new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero))),
            Feature(3, ("t", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))));
        var executor = BuildExecutor(new StubConnector(FederatedSourceKind.EsriRest, rows));

        var query = new FeatureQuery
        {
            TemporalFilter = new TemporalFilter
            {
                PropertyName = "t",
                PropertyType = TemporalPropertyType.DateTime,
                Start = new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero),
                End = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            },
        };

        var result = await executor.ExecuteAsync(Source(FederatedSourceKind.EsriRest), query);

        Assert.Equal(new long[] { 2 }, result.Result.Items.Select(f => f.Id).ToArray());
    }

    [UnitTest]
    public async Task ExecuteAsync_ExactSpatialRelationship_RecordsUnappliedRefinement()
    {
        var rows = ImmutableArray.Create(Feature(1), Feature(2));
        var executor = BuildExecutor(new StubConnector(FederatedSourceKind.EsriRest, rows));

        var query = FeatureQuery.WithSpatialFilter(new SpatialFilter
        {
            Geometry = Array.Empty<byte>(),
            SpatialRelationship = SpatialRelationship.Within,
        });

        var result = await executor.ExecuteAsync(Source(FederatedSourceKind.EsriRest), query);

        Assert.Contains(FederationPredicateKind.SpatialFilter, result.UnappliedLocalRefinements);
        Assert.Equal(2, result.Result.Items.Length);
    }

    [UnitTest]
    public async Task ExecuteAsync_CircuitBreaker_OpensAfterConsecutiveFailures()
    {
        var executor = BuildExecutor(new ThrowingConnector(FederatedSourceKind.EsriRest), FastFail);
        var source = Source(FederatedSourceKind.EsriRest);

        var reasons = new System.Collections.Generic.List<FederatedSourceUnavailableReason>();
        for (var i = 0; i < 4; i++)
        {
            var ex = await Assert.ThrowsAsync<FederatedSourceUnavailableException>(
                () => executor.ExecuteAsync(source, new FeatureQuery()));
            reasons.Add(ex.Reason);
        }

        Assert.Equal(FederatedSourceUnavailableReason.Faulted, reasons[0]);
        Assert.Contains(FederatedSourceUnavailableReason.CircuitOpen, reasons);
        // Once open, the breaker fast-fails instead of contacting the source again.
        Assert.Equal(FederatedSourceUnavailableReason.CircuitOpen, reasons[^1]);
    }

    [UnitTest]
    public async Task ExecuteAsync_SlowSource_TranslatesToTimeout()
    {
        var executor = BuildExecutor(new SlowConnector(FederatedSourceKind.EsriRest), FastFail);
        var source = Source(FederatedSourceKind.EsriRest, TimeSpan.FromMilliseconds(50));

        var ex = await Assert.ThrowsAsync<FederatedSourceUnavailableException>(
            () => executor.ExecuteAsync(source, new FeatureQuery()));

        Assert.Equal(FederatedSourceUnavailableReason.TimedOut, ex.Reason);
    }

    [UnitTest]
    public async Task ExecuteAsync_CallerCancellation_PropagatesWithoutWrapping()
    {
        var executor = BuildExecutor(new SlowConnector(FederatedSourceKind.EsriRest), FastFail);
        var source = Source(FederatedSourceKind.EsriRest);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => executor.ExecuteAsync(source, new FeatureQuery(), cts.Token));
    }

    [UnitTest]
    public async Task ExecuteAsync_MultiSource_MergesResultsAndRecordsFailures()
    {
        var connectors = new MultiplexConnector(FederatedSourceKind.OgcWfs);
        connectors.Rows["src-OgcWfs"] = ImmutableArray.Create(Feature(1, ("name", "z")), Feature(2, ("name", "a")));
        connectors.Failures.Add("failing");

        var executor = BuildExecutor(connectors, FastFail);

        var ok = Source(FederatedSourceKind.OgcWfs);
        var bad = ok with { Id = "failing" };

        var query = new FeatureQuery { OrderBy = ImmutableArray.Create(OrderByClause.Asc("name")) };
        var combined = await executor.ExecuteAsync(ImmutableArray.Create(ok, bad), query);

        Assert.Single(combined.Sources);
        var failure = Assert.Single(combined.Failures);
        Assert.Equal("failing", failure.SourceId);
        // Cross-source ordering is re-applied over the merged rows.
        Assert.Equal(new long[] { 2, 1 }, combined.Combined.Items.Select(f => f.Id).ToArray());
    }

    [UnitTest]
    public async Task ExecuteAsync_MultiSource_SkipsDisabledSources()
    {
        var connector = new StubConnector(FederatedSourceKind.OgcWfs, ImmutableArray.Create(Feature(1)));
        var executor = BuildExecutor(connector, FastFail);

        var disabled = Source(FederatedSourceKind.OgcWfs) with { Enabled = false };
        var combined = await executor.ExecuteAsync(ImmutableArray.Create(disabled), new FeatureQuery());

        Assert.Empty(combined.Sources);
        Assert.Empty(combined.Failures);
        Assert.Empty(combined.Combined.Items);
    }

    private sealed class StubConnector(FederatedSourceKind kind, ImmutableArray<Feature> rows) : IFederatedSourceConnector
    {
        public FederatedSourceKind Kind => kind;

        public Task<ImmutableArray<Feature>> FetchAsync(FederatedFetchRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(rows);
    }

    private sealed class ThrowingConnector(FederatedSourceKind kind) : IFederatedSourceConnector
    {
        public FederatedSourceKind Kind => kind;

        public Task<ImmutableArray<Feature>> FetchAsync(FederatedFetchRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("remote down");
    }

    private sealed class SlowConnector(FederatedSourceKind kind) : IFederatedSourceConnector
    {
        public FederatedSourceKind Kind => kind;

        public async Task<ImmutableArray<Feature>> FetchAsync(FederatedFetchRequest request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            return ImmutableArray<Feature>.Empty;
        }
    }

    private sealed class MultiplexConnector(FederatedSourceKind kind) : IFederatedSourceConnector
    {
        public FederatedSourceKind Kind => kind;

        public System.Collections.Generic.Dictionary<string, ImmutableArray<Feature>> Rows { get; } = new();

        public System.Collections.Generic.HashSet<string> Failures { get; } = new();

        public Task<ImmutableArray<Feature>> FetchAsync(FederatedFetchRequest request, CancellationToken cancellationToken)
        {
            if (Failures.Contains(request.Source.Id))
            {
                throw new InvalidOperationException("remote down");
            }

            return Task.FromResult(Rows.TryGetValue(request.Source.Id, out var rows) ? rows : ImmutableArray<Feature>.Empty);
        }
    }
}
