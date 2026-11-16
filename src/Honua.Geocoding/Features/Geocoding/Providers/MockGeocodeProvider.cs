// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Geocoding.Features.Geocoding.Abstractions;
using Honua.Geocoding.Features.Geocoding.Domain;

namespace Honua.Geocoding.Features.Geocoding.Providers;

internal sealed class MockGeocodeProvider : BaseGeocodeProvider
{
    public const string ProviderName = "mock";

    private readonly MockGeocodeProviderConfiguration _configuration;

    public MockGeocodeProvider(MockGeocodeProviderConfiguration? configuration = null)
    {
        _configuration = configuration ?? new MockGeocodeProviderConfiguration();
    }

    public override string Name => ProviderName;

    public override GeocodeProviderCapabilities Capabilities => new(
        SupportsForwardGeocode: true,
        SupportsReverseGeocode: true,
        SupportsSuggest: _configuration.EnableSuggest,
        SupportsBatch: _configuration.EnableBatch,
        SupportsStructuredInput: false,
        SupportsBiasing: true)
    {
        SupportedSpatialReferences = [4326, 3857],
        MaxResultsPerRequest = _configuration.MaxResults,
        MaxBatchSize = _configuration.MaxBatchSize,
        RequiresAuthentication = false,
        DefaultTimeoutSeconds = _configuration.TimeoutSeconds
    };

    public override Task<IReadOnlyList<GeocodeCandidate>> ForwardGeocodeAsync(
        ForwardGeocodeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_configuration.SimulateFailure)
        {
            throw new GeocodeProviderException("Mock provider simulated failure")
            {
                ProviderName = Name,
                ErrorCode = GeocodeErrorCodes.ServiceUnavailable
            };
        }

        // Simulate processing delay
        if (_configuration.DelayMs > 0)
        {
            return Task.Delay(_configuration.DelayMs, cancellationToken)
                .ContinueWith<IReadOnlyList<GeocodeCandidate>>(_ => CreateMockForwardResults(request), cancellationToken);
        }

        return Task.FromResult<IReadOnlyList<GeocodeCandidate>>(CreateMockForwardResults(request));
    }

    public override Task<ReverseGeocodeMatch?> ReverseGeocodeAsync(
        ReverseGeocodeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_configuration.SimulateFailure)
        {
            throw new GeocodeProviderException("Mock provider simulated failure")
            {
                ProviderName = Name,
                ErrorCode = GeocodeErrorCodes.ServiceUnavailable
            };
        }

        // Simulate processing delay
        if (_configuration.DelayMs > 0)
        {
            return Task.Delay(_configuration.DelayMs, cancellationToken)
                .ContinueWith(_ => CreateMockReverseResult(request), cancellationToken);
        }

        return Task.FromResult(CreateMockReverseResult(request));
    }

    protected override Task<IReadOnlyList<GeocodeSuggestion>> SuggestCoreAsync(
        SuggestGeocodeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_configuration.SimulateFailure)
        {
            throw new GeocodeProviderException("Mock provider simulated failure")
            {
                ProviderName = Name,
                ErrorCode = GeocodeErrorCodes.ServiceUnavailable
            };
        }

        // Simulate processing delay
        if (_configuration.DelayMs > 0)
        {
            return Task.Delay(_configuration.DelayMs, cancellationToken)
                .ContinueWith<IReadOnlyList<GeocodeSuggestion>>(_ => CreateMockSuggestions(request), cancellationToken);
        }

        return Task.FromResult<IReadOnlyList<GeocodeSuggestion>>(CreateMockSuggestions(request));
    }

    protected override Task<IReadOnlyList<GeocodeCandidate>> BatchGeocodeCoreAsync(
        BatchGeocodeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_configuration.SimulateFailure)
        {
            throw new GeocodeProviderException("Mock provider simulated failure")
            {
                ProviderName = Name,
                ErrorCode = GeocodeErrorCodes.ServiceUnavailable
            };
        }

        // Simulate processing delay
        if (_configuration.DelayMs > 0)
        {
            return Task.Delay(_configuration.DelayMs, cancellationToken)
                .ContinueWith<IReadOnlyList<GeocodeCandidate>>(_ => CreateMockBatchResults(request), cancellationToken);
        }

        return Task.FromResult<IReadOnlyList<GeocodeCandidate>>(CreateMockBatchResults(request));
    }

    protected override Task CheckHealthCoreAsync(CancellationToken cancellationToken = default)
    {
        if (_configuration.SimulateFailure)
        {
            throw new GeocodeProviderException("Mock provider health check failed")
            {
                ProviderName = Name,
                ErrorCode = GeocodeErrorCodes.ServiceUnavailable
            };
        }

        return Task.CompletedTask;
    }

    private List<GeocodeCandidate> CreateMockForwardResults(ForwardGeocodeRequest request)
    {
        var maxResults = ValidateMaxResults(request.MaxResults);
        var results = new List<GeocodeCandidate>();

        for (int i = 0; i < Math.Min(maxResults, _configuration.ResultCount); i++)
        {
            var score = 100.0 - (i * 10.0); // Decreasing score
            var x = -122.4194 + (i * 0.01); // Mock coordinates around San Francisco
            var y = 37.7749 + (i * 0.01);

            var attributes = BuildAttributes(
                providerId: $"mock_{i}",
                additionalAttributes: new Dictionary<string, string?>
                {
                    ["Match_addr"] = $"{request.Query} (Mock Result {i + 1})",
                    ["Score"] = score.ToString("F1", CultureInfo.InvariantCulture),
                    ["AddressType"] = i == 0 ? "PointAddress" : "StreetAddress"
                });

            results.Add(new GeocodeCandidate(
                Address: $"{request.Query} (Mock Result {i + 1})",
                X: x,
                Y: y,
                Score: score,
                Attributes: attributes,
                ProviderId: $"mock_{i}")
            {
                MatchLevel = i == 0 ? "exact" : "approximate",
                AddressType = i == 0 ? "PointAddress" : "StreetAddress",
                SpatialReferenceWkid = request.SpatialReferenceWkid
            });
        }

        return results;
    }

    private ReverseGeocodeMatch? CreateMockReverseResult(ReverseGeocodeRequest request)
    {
        if (_configuration.ReturnNoResults)
        {
            return null;
        }

        var attributes = BuildAttributes(
            providerId: "mock_reverse",
            additionalAttributes: new Dictionary<string, string?>
            {
                ["Match_addr"] = "123 Mock Street, Mock City, MC 12345",
                ["AddressType"] = "PointAddress"
            });

        return new ReverseGeocodeMatch(
            Address: "123 Mock Street, Mock City, MC 12345",
            X: request.X,
            Y: request.Y,
            Attributes: attributes,
            ProviderId: "mock_reverse")
        {
            AddressType = "PointAddress",
            SpatialReferenceWkid = request.SpatialReferenceWkid,
            StructuredAddress = new StructuredAddress
            {
                AddressNumber = "123",
                StreetName = "Mock Street",
                City = "Mock City",
                Region = "MC",
                PostalCode = "12345",
                Country = "Mock Country"
            }
        };
    }

    private List<GeocodeSuggestion> CreateMockSuggestions(SuggestGeocodeRequest request)
    {
        var maxResults = ValidateMaxResults(request.MaxResults);
        var suggestions = new List<GeocodeSuggestion>();

        for (int i = 0; i < Math.Min(maxResults, _configuration.ResultCount); i++)
        {
            suggestions.Add(new GeocodeSuggestion(
                Text: $"{request.Text} Mock Suggestion {i + 1}",
                MagicKey: $"mock_suggestion_{i}",
                IsCollection: i % 3 == 0)
            {
                Category = i % 2 == 0 ? "Address" : "POI"
            });
        }

        return suggestions;
    }

    private List<GeocodeCandidate> CreateMockBatchResults(BatchGeocodeRequest request)
    {
        var results = new List<GeocodeCandidate>();

        for (int queryIndex = 0; queryIndex < request.Queries.Count; queryIndex++)
        {
            var query = request.Queries[queryIndex];
            var x = -122.4194 + (queryIndex * 0.1);
            var y = 37.7749 + (queryIndex * 0.1);

            var attributes = BuildAttributes(
                providerId: $"mock_batch_{queryIndex}",
                additionalAttributes: new Dictionary<string, string?>
                {
                    ["Match_addr"] = $"{query} (Batch Result)",
                    ["BatchIndex"] = queryIndex.ToString(CultureInfo.InvariantCulture),
                    // Stamp the ResultID correlation attribute so positional alignment survives
                    // even when callers reorder or filter the locations array (matches the Esri
                    // geocodeAddresses contract and the BaseGeocodeProvider fan-out behaviour).
                    [GeocodeCandidate.ResultIdAttribute] = queryIndex.ToString(CultureInfo.InvariantCulture)
                });

            results.Add(new GeocodeCandidate(
                Address: $"{query} (Batch Result)",
                X: x,
                Y: y,
                Score: 95.0,
                Attributes: attributes,
                ProviderId: $"mock_batch_{queryIndex}")
            {
                SpatialReferenceWkid = request.SpatialReferenceWkid
            });
        }

        return results;
    }
}

public sealed record MockGeocodeProviderConfiguration : GeocodeProviderConfiguration
{
    public bool EnableSuggest { get; init; } = true;

    public bool EnableBatch { get; init; } = true;

    public int MaxBatchSize { get; init; } = 100;

    public bool SimulateFailure { get; init; }

    public bool ReturnNoResults { get; init; }

    public int DelayMs { get; init; }

    public int ResultCount { get; init; } = 3;
}
