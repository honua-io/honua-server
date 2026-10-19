// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using Honua.Geocoding.Features.Geocoding.Abstractions;
using Honua.Geocoding.Features.Geocoding.Domain;
using Honua.Geocoding.Features.Geocoding.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Core.Tests.Features.Geocoding;

/// <summary>
/// Verifies PA-200:
/// <see cref="GeocodeCoordinatorService.SuggestAsync(SuggestGeocodeRequest, string, CancellationToken)"/> and
/// <see cref="GeocodeCoordinatorService.BatchGeocodeAsync(BatchGeocodeRequest, string, CancellationToken)"/> emit
/// "Honua.Geocoding" spans (mirroring the existing Forward/Reverse geocode
/// telemetry) tagged with query length / batch size rather than raw
/// address/query text.
/// </summary>
public sealed class GeocodeCoordinatorTelemetryTests
{
    [Trait("Tier", "Fast")]
    [Fact]
    public async Task SuggestAsync_Success_EmitsSpanTaggedWithQueryLengthNotRawText()
    {
        var provider = new FakeGeocodeProvider(
            "capable",
            new GeocodeProviderCapabilities(SupportsSuggest: true))
        {
            SuggestResult = [new GeocodeSuggestion("123 Main St", "magic-1")]
        };

        var coordinator = CreateCoordinator([provider], "capable", maxFailoverAttempts: 1);

        using var capture = new ActivityCapture();

        const string queryText = "123 Main St, Anytown";
        var result = await coordinator.SuggestAsync(
            new SuggestGeocodeRequest(queryText),
            providerName: null,
            allowFailover: true,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var span = capture.Single("geocoding.suggest");
        Assert.Equal("geocoding.suggest", span.OperationName);
        Assert.Equal(queryText.Length, span.TagObjects.First(t => t.Key == "honua.geocoding.query_length").Value);
        Assert.DoesNotContain(capture.Activities, a => a.TagObjects.Any(t => Equals(t.Value, queryText)));
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task BatchGeocodeAsync_Success_EmitsSpanTaggedWithBatchSize()
    {
        var provider = new FakeGeocodeProvider(
            "capable",
            new GeocodeProviderCapabilities(SupportsBatch: true))
        {
            BatchResult = [new GeocodeCandidate("1 Test St", 1.0, 2.0, 99.0, new Dictionary<string, string?>())]
        };

        var coordinator = CreateCoordinator([provider], "capable", maxFailoverAttempts: 1);

        using var capture = new ActivityCapture();

        var request = new BatchGeocodeRequest(["1 Test St", "2 Test Ave"]);
        var result = await coordinator.BatchGeocodeAsync(
            request,
            providerName: null,
            allowFailover: true,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var span = capture.Single("geocoding.batch");
        Assert.Equal(2, span.TagObjects.First(t => t.Key == "honua.geocoding.batch_size").Value);
        Assert.DoesNotContain(capture.Activities, a => a.TagObjects.Any(t =>
            Equals(t.Value, "1 Test St") || Equals(t.Value, "2 Test Ave")));
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("geocoding.batch")]
    [InlineData("geocoding.suggest")]
    public async Task ActivityCapture_OverlappingForeignInvocation_DoesNotContaminateOwnSpan(string operationName)
    {
        using var capture = new ActivityCapture();
        using var ownSpan = GeocodingTelemetry.Source.StartActivity(operationName);
        Assert.NotNull(ownSpan);

        // A second async flow emits the same operation while our span is still running.
        // Its independent parent models another test collection invoking the coordinator.
        var foreignTrace = await Task.Run(() =>
        {
            using var parent = ActivityCapture.StartIndependentParent();
            using var foreignSpan = GeocodingTelemetry.Source.StartActivity(operationName);
            Assert.NotNull(foreignSpan);
            return foreignSpan.TraceId;
        });

        Assert.NotEqual(ownSpan.TraceId, foreignTrace);
        Assert.Empty(capture.Activities);
        ownSpan.Stop();
        Assert.Same(ownSpan, capture.Single(operationName));
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("geocoding.batch")]
    [InlineData("geocoding.suggest")]
    public async Task ActivityCapture_DuplicateOwnSpans_RemainDetectable(string operationName)
    {
        using var capture = new ActivityCapture();
        await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            using var span = GeocodingTelemetry.Source.StartActivity(operationName);
            Assert.NotNull(span);
        })));

        Assert.Equal(2, capture.Activities.Count);
        Assert.Throws<Xunit.Sdk.SingleException>(() => capture.Single(operationName));
    }

    private sealed class ActivityCapture : IDisposable
    {
        private readonly Activity _parent = StartIndependentParent();
        private readonly ActivityListener _listener;

        public ActivityCapture()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == GeocodingTelemetry.ActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity =>
                {
                    if (activity.TraceId == _parent.TraceId)
                    {
                        Activities.Enqueue(activity);
                    }
                }
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public ConcurrentQueue<Activity> Activities { get; } = new();

        public Activity Single(string operationName) =>
            Assert.Single(Activities, activity => activity.OperationName == operationName);

        public static Activity StartIndependentParent() => new Activity("geocoding-telemetry-test")
            .SetIdFormat(ActivityIdFormat.W3C)
            .SetParentId(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.None)
            .Start();

        public void Dispose()
        {
            _listener.Dispose();
            _parent.Dispose();
        }
    }

    private static GeocodeCoordinatorService CreateCoordinator(
        IReadOnlyList<IGeocodeProvider> providers,
        string defaultProvider,
        int maxFailoverAttempts)
    {
        var configuration = new GeocodingConfiguration
        {
            DefaultProvider = defaultProvider,
            EnableFailover = true,
            MaxFailoverAttempts = maxFailoverAttempts
        };

        var registry = new GeocodeProviderRegistry(
            new EmptyServiceProvider(),
            providers,
            Options.Create(configuration));

        var limitEnforcer = new GeocodeLimitEnforcer(Options.Create(configuration));

        return new GeocodeCoordinatorService(
            registry,
            Options.Create(configuration),
            limitEnforcer,
            NullLogger<GeocodeCoordinatorService>.Instance);
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class FakeGeocodeProvider : IGeocodeProvider
    {
        public FakeGeocodeProvider(string name, GeocodeProviderCapabilities capabilities)
        {
            Name = name;
            Capabilities = capabilities;
        }

        public string Name { get; }

        public GeocodeProviderCapabilities Capabilities { get; }

        public IReadOnlyList<GeocodeSuggestion> SuggestResult { get; init; } = [];

        public IReadOnlyList<GeocodeCandidate> BatchResult { get; init; } = [];

        public Task<IReadOnlyList<GeocodeCandidate>> ForwardGeocodeAsync(
            ForwardGeocodeRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GeocodeCandidate>>([]);

        public Task<ReverseGeocodeMatch?> ReverseGeocodeAsync(
            ReverseGeocodeRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult<ReverseGeocodeMatch?>(null);

        public Task<IReadOnlyList<GeocodeSuggestion>> SuggestAsync(
            SuggestGeocodeRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(SuggestResult);

        public Task<IReadOnlyList<GeocodeCandidate>> BatchGeocodeAsync(
            BatchGeocodeRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(BatchResult);

        public Task<GeocodeProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new GeocodeProviderHealth(Name, true, LastChecked: DateTime.UtcNow));
    }
}
