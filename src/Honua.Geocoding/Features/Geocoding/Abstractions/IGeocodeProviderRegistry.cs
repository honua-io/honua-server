// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Geocoding.Features.Geocoding.Domain;

namespace Honua.Geocoding.Features.Geocoding.Abstractions;

public interface IGeocodeProviderRegistry
{
    void RegisterProvider(IGeocodeProvider provider);

    void RegisterProvider(string providerName, Func<IServiceProvider, IGeocodeProvider> factory);

    IGeocodeProvider? GetProvider(string providerName);

    IReadOnlyList<IGeocodeProvider> GetAllProviders();

    IReadOnlyList<string> GetProvidersByPriority();

    bool IsProviderRegistered(string providerName);

    bool UnregisterProvider(string providerName);
}

public interface IGeocodeCoordinatorService
{
    Task<GeocodeResult<IReadOnlyList<GeocodeCandidate>>> ForwardGeocodeAsync(
        ForwardGeocodeRequest request,
        string? providerName = null,
        CancellationToken cancellationToken = default);

    Task<GeocodeResult<IReadOnlyList<GeocodeCandidate>>> ForwardGeocodeAsync(
        ForwardGeocodeRequest request,
        string? providerName,
        bool allowFailover,
        CancellationToken cancellationToken = default);

    Task<GeocodeResult<ReverseGeocodeMatch?>> ReverseGeocodeAsync(
        ReverseGeocodeRequest request,
        string? providerName = null,
        CancellationToken cancellationToken = default);

    Task<GeocodeResult<ReverseGeocodeMatch?>> ReverseGeocodeAsync(
        ReverseGeocodeRequest request,
        string? providerName,
        bool allowFailover,
        CancellationToken cancellationToken = default);

    Task<GeocodeResult<IReadOnlyList<GeocodeSuggestion>>> SuggestAsync(
        SuggestGeocodeRequest request,
        string? providerName = null,
        CancellationToken cancellationToken = default);

    Task<GeocodeResult<IReadOnlyList<GeocodeSuggestion>>> SuggestAsync(
        SuggestGeocodeRequest request,
        string? providerName,
        bool allowFailover,
        CancellationToken cancellationToken = default);

    Task<GeocodeResult<IReadOnlyList<GeocodeCandidate>>> BatchGeocodeAsync(
        BatchGeocodeRequest request,
        string? providerName = null,
        CancellationToken cancellationToken = default);

    Task<GeocodeResult<IReadOnlyList<GeocodeCandidate>>> BatchGeocodeAsync(
        BatchGeocodeRequest request,
        string? providerName,
        bool allowFailover,
        CancellationToken cancellationToken = default);
}

public sealed record GeocodeResult<T>
{
    public required T Data { get; init; }

    public required string ProviderName { get; init; }

    public bool IsSuccess { get; init; } = true;

    public string? ErrorMessage { get; init; }

    public double? ResponseTimeMs { get; init; }

    public IReadOnlyList<string>? AttemptedProviders { get; init; }

    public IReadOnlyDictionary<string, object>? Metadata { get; init; }

}

public static class GeocodeResults
{
    public static GeocodeResult<T> Success<T>(T data, string providerName, double? responseTimeMs = null)
    {
        return new GeocodeResult<T>
        {
            Data = data,
            ProviderName = providerName,
            IsSuccess = true,
            ResponseTimeMs = responseTimeMs
        };
    }

    public static GeocodeResult<T> Failure<T>(
        string errorMessage,
        string providerName,
        double? responseTimeMs = null,
        IReadOnlyList<string>? attemptedProviders = null)
    {
        return new GeocodeResult<T>
        {
            Data = default!,
            ProviderName = providerName,
            IsSuccess = false,
            ErrorMessage = errorMessage,
            ResponseTimeMs = responseTimeMs,
            AttemptedProviders = attemptedProviders
        };
    }
}
