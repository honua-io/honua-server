// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.Metrics;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Server.Features.Infrastructure.Caching;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Infrastructure.Caching;

/// <summary>
/// Verifies that the canonical hit/miss/eviction counters wired through HonuaCacheMetrics
/// fire for at least one concrete cache call site (MemoryResponseCache).
/// </summary>
[Collection("Unit")]
public sealed class HonuaCacheMetricsTests : IDisposable
{
    private readonly MemoryCache _memoryCache = new(new MemoryCacheOptions());

    public void Dispose()
    {
        _memoryCache.Dispose();
    }

    [Fact]
    public async Task MemoryResponseCache_GetMiss_IncrementsMissCounter()
    {
        // Arrange
        var cache = new MemoryResponseCache(_memoryCache, NullLogger<MemoryResponseCache>.Instance);
        using var collector = MetricsCollector.ForInstrument("honua_cache_misses_total");

        // Act
        var result = await cache.GetAsync<string>("missing-key");

        // Assert
        result.Should().BeNull();
        var sample = collector.Samples.SingleOrDefault(s => GetTag(s, "cache_name") == MemoryResponseCache.CacheName);
        sample.Should().NotBeNull("a miss against MemoryResponseCache must increment honua_cache_misses_total");
        sample!.Value.Should().Be(1);
    }

    [Fact]
    public async Task MemoryResponseCache_GetHit_IncrementsHitCounter()
    {
        // Arrange
        var cache = new MemoryResponseCache(_memoryCache, NullLogger<MemoryResponseCache>.Instance);
        await cache.SetAsync("hot-key", "value", TimeSpan.FromMinutes(1));
        using var collector = MetricsCollector.ForInstrument("honua_cache_hits_total");

        // Act
        var result = await cache.GetAsync<string>("hot-key");

        // Assert
        result.Should().Be("value");
        var sample = collector.Samples.SingleOrDefault(s => GetTag(s, "cache_name") == MemoryResponseCache.CacheName);
        sample.Should().NotBeNull("a hit against MemoryResponseCache must increment honua_cache_hits_total");
        sample!.Value.Should().Be(1);
    }

    [Fact]
    public async Task MemoryResponseCache_Remove_IncrementsEvictionCounter()
    {
        // Arrange
        var cache = new MemoryResponseCache(_memoryCache, NullLogger<MemoryResponseCache>.Instance);
        await cache.SetAsync("evict-key", "value", TimeSpan.FromMinutes(1));
        using var collector = MetricsCollector.ForInstrument("honua_cache_evictions_total");

        // Act
        await cache.RemoveAsync("evict-key");

        // Assert: PostEvictionCallback should fire and bump the evictions counter.
        // Allow a brief settle window because the callback runs on the cache's internal pool.
        for (var i = 0; i < 20 && collector.Samples.Length == 0; i++)
        {
            await Task.Delay(10);
        }

        var sample = collector.Samples.SingleOrDefault(s => GetTag(s, "cache_name") == MemoryResponseCache.CacheName);
        sample.Should().NotBeNull("an explicit Remove must trigger PostEvictionCallback and increment honua_cache_evictions_total");
        sample!.Value.Should().BeGreaterThan(0);
    }

    [Fact]
    public void HonuaCacheMetrics_RecordsWithFallbackCacheNameWhenNullOrEmpty()
    {
        // Arrange
        using var collector = MetricsCollector.ForInstrument("honua_cache_hits_total");

        // Act
        HonuaCacheMetrics.RecordHit(null);
        HonuaCacheMetrics.RecordHit(string.Empty);

        // Assert
        var unknownSamples = collector.Samples.Where(s => GetTag(s, "cache_name") == "unknown").ToArray();
        unknownSamples.Should().HaveCountGreaterThanOrEqualTo(2);
    }

    private static string? GetTag(MeasurementSample sample, string name)
    {
        foreach (var tag in sample.Tags)
        {
            if (tag.Key == name)
            {
                return tag.Value as string;
            }
        }

        return null;
    }

    private sealed record MeasurementSample(long Value, KeyValuePair<string, object?>[] Tags);

    /// <summary>
    /// Test-only MeterListener that captures long measurements emitted by a named
    /// instrument on the "Honua" meter. Disposable to keep parallel test runs isolated.
    /// </summary>
    private sealed class MetricsCollector : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly List<MeasurementSample> _samples = new();
        private readonly object _gate = new();

        private MetricsCollector(MeterListener listener)
        {
            _listener = listener;
        }

        public MeasurementSample[] Samples
        {
            get
            {
                lock (_gate)
                {
                    return _samples.ToArray();
                }
            }
        }

        public static MetricsCollector ForInstrument(string instrumentName)
        {
            var collector = new MetricsCollector(new MeterListener());
            collector._listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == "Honua" && instrument.Name == instrumentName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };
            collector._listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
            {
                lock (collector._gate)
                {
                    collector._samples.Add(new MeasurementSample(measurement, tags.ToArray()));
                }
            });
            collector._listener.Start();
            return collector;
        }

        public void Dispose()
        {
            _listener.Dispose();
        }
    }
}
