// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geocoding.Abstractions;
using Honua.Core.Features.Geocoding.Domain;

namespace Honua.Core.Features.Geocoding.Providers;

/// <summary>
/// Mock geocoding provider for testing and demonstration purposes
/// </summary>
public sealed class MockGeocodeProvider : BaseGeocodeProvider
{
    /// <summary>
    /// Mock provider name constant
    /// </summary>
    public const string ProviderName = "mock";

    private readonly MockGeocodeProviderConfiguration _configuration;

    /// <summary>
    /// Initialize a new mock geocode provider
    /// </summary>
    /// <param name="configuration">Provider configuration</param>
    public MockGeocodeProvider(MockGeocodeProviderConfiguration? configuration = null)
    {
        _configuration = configuration ?? new MockGeocodeProviderConfiguration();
    }

    /// <inheritdoc />
    public override string Name => ProviderName;

    /// <inheritdoc />
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

    /// <inheritdoc />
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
                .ContinueWith(_ => CreateMockForwardResults(request), cancellationToken);
        }

        return Task.FromResult(CreateMockForwardResults(request));
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
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
                .ContinueWith(_ => CreateMockSuggestions(request), cancellationToken);
        }

        return Task.FromResult(CreateMockSuggestions(request));
    }

    /// <inheritdoc />
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
                .ContinueWith(_ => CreateMockBatchResults(request), cancellationToken);
        }

        return Task.FromResult(CreateMockBatchResults(request));
    }

    /// <inheritdoc />
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

    private IReadOnlyList<GeocodeCandidate> CreateMockForwardResults(ForwardGeocodeRequest request)
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
                    ["Score"] = score.ToString("F1"),
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

    private IReadOnlyList<GeocodeSuggestion> CreateMockSuggestions(SuggestGeocodeRequest request)
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

    private IReadOnlyList<GeocodeCandidate> CreateMockBatchResults(BatchGeocodeRequest request)
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
                    ["BatchIndex"] = queryIndex.ToString()
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

/// <summary>
/// Configuration for the mock geocoding provider
/// </summary>
public sealed record MockGeocodeProviderConfiguration : GeocodeProviderConfiguration
{
    /// <summary>
    /// Whether to enable suggest functionality
    /// </summary>
    public bool EnableSuggest { get; init; } = true;

    /// <summary>
    /// Whether to enable batch functionality
    /// </summary>
    public bool EnableBatch { get; init; } = true;

    /// <summary>
    /// Maximum batch size
    /// </summary>
    public int MaxBatchSize { get; init; } = 100;

    /// <summary>
    /// Whether to simulate provider failures
    /// </summary>
    public bool SimulateFailure { get; init; } = false;

    /// <summary>
    /// Whether to return no results
    /// </summary>
    public bool ReturnNoResults { get; init; } = false;

    /// <summary>
    /// Delay in milliseconds to simulate processing time
    /// </summary>
    public int DelayMs { get; init; } = 0;

    /// <summary>
    /// Number of results to return
    /// </summary>
    public int ResultCount { get; init; } = 3;
}