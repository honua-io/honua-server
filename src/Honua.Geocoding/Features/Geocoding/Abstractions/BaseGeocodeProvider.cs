// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.Geocoding.Domain;

namespace Honua.Core.Features.Geocoding.Abstractions;

/// <summary>
/// Base implementation for geocoding providers with common functionality
/// </summary>
public abstract class BaseGeocodeProvider : IGeocodeProvider
{
    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract GeocodeProviderCapabilities Capabilities { get; }

    /// <inheritdoc />
    public abstract Task<IReadOnlyList<GeocodeCandidate>> ForwardGeocodeAsync(
        ForwardGeocodeRequest request,
        CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public abstract Task<ReverseGeocodeMatch?> ReverseGeocodeAsync(
        ReverseGeocodeRequest request,
        CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public virtual Task<IReadOnlyList<GeocodeSuggestion>> SuggestAsync(
        SuggestGeocodeRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!Capabilities.SupportsSuggest)
        {
            return Task.FromResult<IReadOnlyList<GeocodeSuggestion>>([]);
        }

        return SuggestCoreAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public virtual Task<IReadOnlyList<GeocodeCandidate>> BatchGeocodeAsync(
        BatchGeocodeRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!Capabilities.SupportsBatch)
        {
            return Task.FromResult<IReadOnlyList<GeocodeCandidate>>([]);
        }

        return BatchGeocodeCoreAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public virtual async Task<GeocodeProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await CheckHealthCoreAsync(cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            return new GeocodeProviderHealth(
                ProviderName: Name,
                IsHealthy: true,
                LastChecked: DateTime.UtcNow)
            {
                ResponseTimeMs = stopwatch.Elapsed.TotalMilliseconds
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            return new GeocodeProviderHealth(
                ProviderName: Name,
                IsHealthy: false,
                ErrorMessage: ex.Message,
                LastChecked: DateTime.UtcNow)
            {
                ResponseTimeMs = stopwatch.Elapsed.TotalMilliseconds
            };
        }
    }

    /// <summary>
    /// Core implementation of suggest functionality
    /// </summary>
    /// <param name="request">Suggest request</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of suggestions</returns>
    protected virtual Task<IReadOnlyList<GeocodeSuggestion>> SuggestCoreAsync(
        SuggestGeocodeRequest request,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException($"Provider '{Name}' does not support suggest functionality.");
    }

    /// <summary>
    /// Core implementation of batch geocoding functionality.
    /// </summary>
    /// <remarks>
    /// The default implementation fans the batch out to <see cref="ForwardGeocodeAsync"/>,
    /// returning the best candidate for each input address in request order. Providers with a
    /// native batch endpoint should override this for efficiency. This keeps the Esri
    /// <c>geocodeAddresses</c> operation working for providers (such as Nominatim) that only
    /// expose a single-address forward-geocode API.
    /// </remarks>
    /// <param name="request">Batch geocoding request</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of geocoding candidates, one per input address in request order</returns>
    protected virtual async Task<IReadOnlyList<GeocodeCandidate>> BatchGeocodeCoreAsync(
        BatchGeocodeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var results = new List<GeocodeCandidate>(request.Queries.Count);

        foreach (var query in request.Queries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(query))
            {
                continue;
            }

            var forwardRequest = new ForwardGeocodeRequest(
                Query: query,
                MaxResults: Math.Max(1, request.MaxResultsPerQuery),
                SpatialReferenceWkid: request.SpatialReferenceWkid,
                CountryCodes: request.CountryCodes);

            var candidates = await ForwardGeocodeAsync(forwardRequest, cancellationToken).ConfigureAwait(false);
            if (candidates.Count > 0)
            {
                results.Add(candidates[0]);
            }
        }

        return results;
    }

    /// <summary>
    /// Core implementation of health check functionality
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the health check</returns>
    protected virtual Task CheckHealthCoreAsync(CancellationToken cancellationToken = default)
    {
        // Default implementation - providers can override for custom health checks
        return Task.CompletedTask;
    }

    /// <summary>
    /// Validate spatial reference system
    /// </summary>
    /// <param name="wkid">WKID to validate</param>
    /// <returns>True if supported, false otherwise</returns>
    protected virtual bool ValidateSpatialReference(int wkid)
    {
        return Capabilities.SupportedSpatialReferences.Length == 0 ||
               Capabilities.SupportedSpatialReferences.Contains(wkid);
    }

    /// <summary>
    /// Validate max results parameter
    /// </summary>
    /// <param name="maxResults">Max results to validate</param>
    /// <returns>Clamped max results value</returns>
    protected virtual int ValidateMaxResults(int maxResults)
    {
        return Math.Clamp(maxResults, 1, Capabilities.MaxResultsPerRequest);
    }

    /// <summary>
    /// Normalize score to 0-100 range
    /// </summary>
    /// <param name="rawScore">Raw score from provider</param>
    /// <param name="minScore">Minimum possible score</param>
    /// <param name="maxScore">Maximum possible score</param>
    /// <returns>Normalized score (0-100)</returns>
    protected static double NormalizeScore(double rawScore, double minScore = 0.0, double maxScore = 1.0)
    {
        if (maxScore <= minScore)
        {
            return 0.0;
        }

        var normalized = (rawScore - minScore) / (maxScore - minScore);
        return Math.Clamp(normalized * 100.0, 0.0, 100.0);
    }

    /// <summary>
    /// Build common attributes dictionary for results
    /// </summary>
    /// <param name="providerId">Provider-specific ID</param>
    /// <param name="additionalAttributes">Additional attributes to include</param>
    /// <returns>Attributes dictionary</returns>
    protected Dictionary<string, string?> BuildAttributes(
        string? providerId = null,
        IReadOnlyDictionary<string, string?>? additionalAttributes = null)
    {
        var attributes = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Provider"] = Name
        };

        if (!string.IsNullOrWhiteSpace(providerId))
        {
            attributes["ProviderId"] = providerId;
        }

        if (additionalAttributes is { Count: > 0 })
        {
            foreach (var kvp in additionalAttributes)
            {
                if (!string.IsNullOrWhiteSpace(kvp.Key))
                {
                    attributes[kvp.Key] = kvp.Value;
                }
            }
        }

        return attributes;
    }
}
