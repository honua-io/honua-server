// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Geocoding.Features.Geocoding.Abstractions;
using Honua.Geocoding.Features.Geocoding.Domain;
using Honua.Geocoding.Features.Geocoding.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Core.Tests.Features.Geocoding;

/// <summary>
/// Verifies PA-200: <see cref="GeocodeCoordinatorService.SuggestAsync"/> and
/// <see cref="GeocodeCoordinatorService.BatchGeocodeAsync"/> emit
/// "Honua.Geocoding" spans (mirroring the existing Forward/Reverse geocode
/// telemetry) tagged with query length / batch size rather than raw
/// address/query text.
/// </summary>
public sealed class GeocodeCoordinatorTelemetryTests
{
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

        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == GeocodingTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add
        };
        ActivitySource.AddActivityListener(listener);

        const string queryText = "123 Main St, Anytown";
        var result = await coordinator.SuggestAsync(
            new SuggestGeocodeRequest(queryText),
            providerName: null,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var span = Assert.Single(activities);
        Assert.Equal("geocoding.suggest", span.OperationName);
        Assert.Equal(queryText.Length, span.TagObjects.First(t => t.Key == "honua.geocoding.query_length").Value);
        Assert.DoesNotContain(activities, a => a.TagObjects.Any(t => Equals(t.Value, queryText)));
    }

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

        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == GeocodingTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add
        };
        ActivitySource.AddActivityListener(listener);

        var request = new BatchGeocodeRequest(["1 Test St", "2 Test Ave"]);
        var result = await coordinator.BatchGeocodeAsync(request, providerName: null, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var span = Assert.Single(
            activities,
            activity => activity.OperationName == "geocoding.batch");
        Assert.Equal(2, span.TagObjects.First(t => t.Key == "honua.geocoding.batch_size").Value);
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
