// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Geocoding.Features.Geocoding.Domain;

namespace Honua.Geocoding.Features.Geocoding.Abstractions;

internal abstract class BaseGeocodeProvider : IGeocodeProvider
{
    public abstract string Name { get; }

    public abstract GeocodeProviderCapabilities Capabilities { get; }

    public abstract Task<IReadOnlyList<GeocodeCandidate>> ForwardGeocodeAsync(
        ForwardGeocodeRequest request,
        CancellationToken cancellationToken = default);

    public abstract Task<ReverseGeocodeMatch?> ReverseGeocodeAsync(
        ReverseGeocodeRequest request,
        CancellationToken cancellationToken = default);

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
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            stopwatch.Stop();

            // No ILogger is threaded through this abstract base (none of the five
            // concrete providers currently accept one; PA-067/PA-201), so the
            // exception detail is surfaced in the returned health record itself —
            // that is the only diagnostic signal available to callers here.
            return new GeocodeProviderHealth(
                ProviderName: Name,
                IsHealthy: false,
                ErrorMessage: $"Provider health check failed: {ex.GetType().Name}: {ex.Message}",
                LastChecked: DateTime.UtcNow)
            {
                ResponseTimeMs = stopwatch.Elapsed.TotalMilliseconds
            };
        }
    }

    protected virtual Task<IReadOnlyList<GeocodeSuggestion>> SuggestCoreAsync(
        SuggestGeocodeRequest request,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException($"Provider '{Name}' does not support suggest functionality.");
    }

    protected virtual async Task<IReadOnlyList<GeocodeCandidate>> BatchGeocodeCoreAsync(
        BatchGeocodeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Capabilities.MaxBatchSize > 0 && request.Queries.Count > Capabilities.MaxBatchSize)
        {
            throw new GeocodeRequestException(
                $"Batch size {request.Queries.Count} exceeds the maximum allowed batch size of {Capabilities.MaxBatchSize} for geocoding provider '{Name}'.")
            {
                ParameterName = "records",
            };
        }

        var results = new List<GeocodeCandidate>(request.Queries.Count);

        for (var index = 0; index < request.Queries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var query = request.Queries[index];

            if (string.IsNullOrWhiteSpace(query))
            {
                results.Add(GeocodeCandidate.CreateNoMatch(index, request.SpatialReferenceWkid));
                continue;
            }

            // Request up to MaxResultsPerQuery candidates for this input and stamp every emitted
            // candidate with the input's zero-based index (ResultID) so callers can correlate each
            // result back to its submitted record even when an input yields several candidates. An
            // input that yields zero candidates still occupies exactly one explicit "no match" slot,
            // so the result list never drops below one entry per input and stays in request order.
            var forwardRequest = new ForwardGeocodeRequest(
                Query: query,
                MaxResults: Math.Max(1, request.MaxResultsPerQuery),
                SpatialReferenceWkid: request.SpatialReferenceWkid,
                CountryCodes: request.CountryCodes);

            var candidates = await ForwardGeocodeAsync(forwardRequest, cancellationToken).ConfigureAwait(false);
            if (candidates.Count == 0)
            {
                results.Add(GeocodeCandidate.CreateNoMatch(index, request.SpatialReferenceWkid));
                continue;
            }

            foreach (var candidate in candidates)
            {
                results.Add(candidate.WithResultId(index));
            }
        }

        return results;
    }

    protected virtual Task CheckHealthCoreAsync(CancellationToken cancellationToken = default)
    {
        // Default implementation - providers can override for custom health checks
        return Task.CompletedTask;
    }

    protected virtual bool ValidateSpatialReference(int wkid)
    {
        return Capabilities.SupportedSpatialReferences.Length == 0 ||
               Capabilities.SupportedSpatialReferences.Contains(wkid);
    }

    protected virtual int ValidateMaxResults(int maxResults)
    {
        return Math.Clamp(maxResults, 1, Capabilities.MaxResultsPerRequest);
    }

    protected static double NormalizeScore(double rawScore, double minScore = 0.0, double maxScore = 1.0)
    {
        if (maxScore <= minScore)
        {
            return 0.0;
        }

        var normalized = (rawScore - minScore) / (maxScore - minScore);
        return Math.Clamp(normalized * 100.0, 0.0, 100.0);
    }

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
            foreach (var kvp in additionalAttributes.Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key)))
            {
                attributes[kvp.Key] = kvp.Value;
            }
        }

        return attributes;
    }
}
