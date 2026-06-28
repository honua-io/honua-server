// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.Metrics;
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
/// Unit tests for the per-source federation latency/error metrics (issue #341 acceptance
/// criterion: "Latency and error metrics per federated source"). A <see cref="MeterListener"/>
/// captures the instruments the executor records so the tests assert the metric tags without a
/// live exporter.
/// </summary>
public sealed class FederationMetricsTests
{
    private static readonly ResiliencePolicyOptions FastFail = new()
    {
        MaxRetryAttempts = 0,
        CircuitBreakerFailures = 2,
        CircuitBreakDuration = TimeSpan.FromMinutes(5),
    };

    private static FederatedSourceDescriptor Source(FederatedSourceKind kind) => new()
    {
        Id = $"src-{kind}",
        DisplayName = kind.ToString(),
        Kind = kind,
        Endpoint = new Uri("https://remote.example.com/"),
        RemoteLayer = "0",
        RequestTimeout = TimeSpan.FromSeconds(30),
    };

    [UnitTest]
    public async Task ExecuteAsync_Success_RecordsDurationAndSuccessOutcome()
    {
        using var capture = new MetricCapture();
        var rows = ImmutableArray.Create(
            new Feature { Id = 1, Geometry = null, Attributes = ImmutableDictionary<string, object?>.Empty });
        var executor = BuildExecutor(new StubConnector(FederatedSourceKind.HonuaGrpc, rows), capture.Metrics);

        await executor.ExecuteAsync(Source(FederatedSourceKind.HonuaGrpc), new FeatureQuery());
        capture.Flush();

        var request = Assert.Single(capture.Measurements.Where(m => m.Instrument == "honua_federation_source_requests_total"));
        Assert.Equal("success", request.Tags["outcome"]);
        Assert.Equal("src-HonuaGrpc", request.Tags["source.id"]);
        Assert.Equal("HonuaGrpc", request.Tags["source.kind"]);

        Assert.Contains(capture.Measurements, m => m.Instrument == "honua_federation_source_duration_ms");
        Assert.Contains(capture.Measurements, m => m.Instrument == "honua_federation_source_rows_total" && m.Value >= 1);
    }

    [UnitTest]
    public async Task ExecuteAsync_NoConnector_RecordsNoConnectorOutcome()
    {
        using var capture = new MetricCapture();
        var executor = BuildExecutor(new StubConnector(FederatedSourceKind.HonuaGrpc, ImmutableArray<Feature>.Empty), capture.Metrics);

        await Assert.ThrowsAsync<FederatedSourceUnavailableException>(
            () => executor.ExecuteAsync(Source(FederatedSourceKind.EsriRest), new FeatureQuery()));
        capture.Flush();

        var request = Assert.Single(capture.Measurements.Where(m => m.Instrument == "honua_federation_source_requests_total"));
        Assert.Equal("no_connector", request.Tags["outcome"]);
    }

    [UnitTest]
    public async Task ExecuteAsync_RemoteFault_RecordsFaultedOutcome()
    {
        using var capture = new MetricCapture();
        var executor = BuildExecutor(new ThrowingConnector(FederatedSourceKind.EsriRest), capture.Metrics);

        await Assert.ThrowsAsync<FederatedSourceUnavailableException>(
            () => executor.ExecuteAsync(Source(FederatedSourceKind.EsriRest), new FeatureQuery()));
        capture.Flush();

        Assert.Contains(
            capture.Measurements,
            m => m.Instrument == "honua_federation_source_requests_total" && (string)m.Tags["outcome"]! == "faulted");
    }

    private static FederatedQueryExecutor BuildExecutor(IFederatedSourceConnector connector, FederationMetrics metrics)
    {
        var services = new ServiceCollection();
        services.AddFederationCore();
        var planner = services.BuildServiceProvider().GetRequiredService<IFederationQueryPlanner>();

        return new FederatedQueryExecutor(
            planner,
            new[] { connector },
            FastFail,
            NullLogger<FederatedQueryExecutor>.Instance,
            metrics);
    }

    private sealed record CapturedMeasurement(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags);

    private sealed class MetricCapture : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly ConcurrentQueue<CapturedMeasurement> _measurements = new();

        public MetricCapture()
        {
            Metrics = new FederationMetrics();
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == FederationMetrics.MeterName)
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };

            _listener.SetMeasurementEventCallback<double>(OnDouble);
            _listener.SetMeasurementEventCallback<long>(OnLong);
            _listener.Start();
        }

        public FederationMetrics Metrics { get; }

        public IReadOnlyCollection<CapturedMeasurement> Measurements => _measurements.ToArray();

        public void Flush() => _listener.RecordObservableInstruments();

        public void Dispose()
        {
            _listener.Dispose();
            Metrics.Dispose();
        }

        private void OnDouble(Instrument instrument, double measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
            => Record(instrument, measurement, tags);

        private void OnLong(Instrument instrument, long measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
            => Record(instrument, measurement, tags);

        private void Record(Instrument instrument, double measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var map = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var tag in tags)
            {
                map[tag.Key] = tag.Value;
            }

            _measurements.Enqueue(new CapturedMeasurement(instrument.Name, measurement, map));
        }
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
}
