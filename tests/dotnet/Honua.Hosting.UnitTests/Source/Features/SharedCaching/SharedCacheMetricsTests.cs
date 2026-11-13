// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.SharedCaching;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Honua.Hosting.UnitTests.Features.SharedCaching;

public sealed class SharedCacheMetricsTests : IDisposable
{
    private readonly Activity _activity = new Activity("cache-metric-test").Start();
    private readonly MeterListener _listener = new();
    private readonly List<Sample> _samples = [];

    public SharedCacheMetricsTests()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "Honua" && instrument.Name.StartsWith("honua_shared_cache_", StringComparison.Ordinal))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Capture(instrument, value, tags));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Capture(instrument, value, tags));
        _listener.Start();
    }

    [UnitTheory]
    [InlineData("hit")]
    [InlineData("miss")]
    [InlineData("bypass")]
    public void Apply_RecordsOneOutputOutcomeWithoutRequestIdentifiers(string outcome)
    {
        var context = SharedCacheTestContexts.GrantedTile();
        context.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(
            new SharedCacheClassMetadata(ResponseCacheClass.Tile), new OutputCacheAttribute()), "tile"));
        var tracked = new OutputCacheContext { HttpContext = context, AllowCacheLookup = true };
        OutputCacheStatus.Track(tracked);
        if (outcome == "hit")
        {
            OutputCacheStatus.RecordHit(tracked);
        }
        if (outcome == "bypass")
        {
            context.Request.Headers.Authorization = "Bearer credential";
        }
        OutputCacheStatus.Apply(context);
        OutputCacheStatus.Apply(context);
        var sample = _samples.Should().ContainSingle().Which;
        sample.Name.Should().Be("honua_shared_cache_output_requests_total");
        sample.Value.Should().Be(1);
        sample.Tags.Should().BeEquivalentTo(new Dictionary<string, object?>
        {
            ["cache_class"] = "Tile",
            ["outcome"] = outcome,
        });
    }

    [UnitTest]
    public void Apply_UndeclaredRoute_DoesNotEmitACacheMetric()
    {
        OutputCacheStatus.Apply(new DefaultHttpContext());
        _samples.Should().BeEmpty();
    }

    [UnitTheory]
    [InlineData(CdnInvalidationOutcome.Submitted)]
    [InlineData(CdnInvalidationOutcome.Failed)]
    [InlineData(CdnInvalidationOutcome.NotConfigured)]
    public async Task InvalidateNowAsync_RecordsConfiguredPurgeOutcomeAndLatency(CdnInvalidationOutcome outcome)
    {
        await Service(new ResultInvalidator(outcome)).InvalidateNowAsync(["/tiles/private-layer/*"],
            CancellationToken.None, CdnPurgeCause.Admin("private-change-id"));
        if (outcome == CdnInvalidationOutcome.NotConfigured)
        {
            _samples.Should().BeEmpty();
            return;
        }
        _samples.Select(sample => sample.Name).Should().BeEquivalentTo(
            "honua_shared_cache_purge_requests_total", "honua_shared_cache_purge_duration_seconds");
        foreach (var sample in _samples)
        {
            sample.Tags.Should().BeEquivalentTo(new Dictionary<string, object?>
            {
                ["reason"] = "admin",
                ["scope"] = "paths",
                ["outcome"] = outcome.ToString().ToLowerInvariant(),
            });
            sample.Value.Should().BeGreaterThanOrEqualTo(0);
        }
    }

    [UnitTest]
    public async Task InvalidateNowAsync_TagBatches_RecordEachSubmission()
    {
        var paths = Enumerable.Range(0, 1001).Select(index => $"#tag-{index}").ToArray();
        await Service(new ResultInvalidator(CdnInvalidationOutcome.Submitted))
            .InvalidateNowAsync(paths, CancellationToken.None, CdnPurgeCause.Edit("change"));
        var counters = _samples.Where(sample => sample.Name.EndsWith("requests_total", StringComparison.Ordinal)).ToArray();
        counters.Should().HaveCount(2);
        counters.Should().OnlyContain(sample => sample.Value == 1 && (string?)sample.Tags["scope"] == "tags");
    }

    [UnitTest]
    public async Task InvalidateNowAsync_Cancellation_RecordsCanceledAndPropagates()
    {
        var action = () => Service(new CancelingInvalidator()).InvalidateNowAsync(CdnPaths.Everything,
            CancellationToken.None, CdnPurgeCause.Operation("operation"));
        await action.Should().ThrowAsync<OperationCanceledException>();
        _samples.Should().HaveCount(2);
        _samples.Should().OnlyContain(sample => (string?)sample.Tags["scope"] == "all"
            && (string?)sample.Tags["outcome"] == "canceled");
    }

    private static CdnInvalidationService Service(ICdnInvalidator invalidator)
        => new(invalidator, NullLogger<CdnInvalidationService>.Instance);

    private void Capture(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (Activity.Current?.Id == _activity.Id)
        {
            _samples.Add(new Sample(instrument.Name, value, tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value)));
        }
    }

    public void Dispose()
    {
        _listener.Dispose();
        _activity.Dispose();
    }

    private sealed record Sample(string Name, double Value, Dictionary<string, object?> Tags);

    private sealed class ResultInvalidator(CdnInvalidationOutcome outcome) : ICdnInvalidator
    {
        public Task<CdnInvalidationResult> InvalidateAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default)
            => Task.FromResult(new CdnInvalidationResult(outcome));
    }

    private sealed class CancelingInvalidator : ICdnInvalidator
    {
        public Task<CdnInvalidationResult> InvalidateAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default)
            => Task.FromCanceled<CdnInvalidationResult>(new CancellationToken(true));
    }
}
