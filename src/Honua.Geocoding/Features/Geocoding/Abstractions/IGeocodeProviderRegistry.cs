// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geocoding.Domain;

namespace Honua.Core.Features.Geocoding.Abstractions;

/// <summary>
/// Registry for managing geocoding providers
/// </summary>
public interface IGeocodeProviderRegistry
{
    /// <summary>
    /// Register a geocoding provider
    /// </summary>
    /// <param name="provider">Provider to register</param>
    void RegisterProvider(IGeocodeProvider provider);

    /// <summary>
    /// Register a geocoding provider factory
    /// </summary>
    /// <param name="providerName">Name of the provider</param>
    /// <param name="factory">Factory function to create the provider</param>
    void RegisterProvider(string providerName, Func<IServiceProvider, IGeocodeProvider> factory);

    /// <summary>
    /// Get a provider by name
    /// </summary>
    /// <param name="providerName">Name of the provider</param>
    /// <returns>Provider instance or null if not found</returns>
    IGeocodeProvider? GetProvider(string providerName);

    /// <summary>
    /// Get all registered providers
    /// </summary>
    /// <returns>List of all registered providers</returns>
    IReadOnlyList<IGeocodeProvider> GetAllProviders();

    /// <summary>
    /// Get provider names in priority order
    /// </summary>
    /// <returns>List of provider names ordered by priority</returns>
    IReadOnlyList<string> GetProvidersByPriority();

    /// <summary>
    /// Check if a provider is registered
    /// </summary>
    /// <param name="providerName">Name of the provider</param>
    /// <returns>True if registered, false otherwise</returns>
    bool IsProviderRegistered(string providerName);

    /// <summary>
    /// Unregister a provider
    /// </summary>
    /// <param name="providerName">Name of the provider to unregister</param>
    /// <returns>True if unregistered, false if not found</returns>
    bool UnregisterProvider(string providerName);
}

/// <summary>
/// Service for coordinating geocoding operations across multiple providers
/// </summary>
public interface IGeocodeCoordinatorService
{
    /// <summary>
    /// Perform forward geocoding with automatic provider selection and failover
    /// </summary>
    /// <param name="request">Forward geocoding request</param>
    /// <param name="providerName">Preferred provider name (optional)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Geocoding results</returns>
    Task<GeocodeResult<IReadOnlyList<GeocodeCandidate>>> ForwardGeocodeAsync(
        ForwardGeocodeRequest request,
        string? providerName = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Perform reverse geocoding with automatic provider selection and failover
    /// </summary>
    /// <param name="request">Reverse geocoding request</param>
    /// <param name="providerName">Preferred provider name (optional)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Reverse geocoding result</returns>
    Task<GeocodeResult<ReverseGeocodeMatch?>> ReverseGeocodeAsync(
        ReverseGeocodeRequest request,
        string? providerName = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get suggestions with automatic provider selection and failover
    /// </summary>
    /// <param name="request">Suggest request</param>
    /// <param name="providerName">Preferred provider name (optional)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Suggestions result</returns>
    Task<GeocodeResult<IReadOnlyList<GeocodeSuggestion>>> SuggestAsync(
        SuggestGeocodeRequest request,
        string? providerName = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Perform batch geocoding with automatic provider selection and failover
    /// </summary>
    /// <param name="request">Batch geocoding request</param>
    /// <param name="providerName">Preferred provider name (optional)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Batch geocoding results</returns>
    Task<GeocodeResult<IReadOnlyList<GeocodeCandidate>>> BatchGeocodeAsync(
        BatchGeocodeRequest request,
        string? providerName = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result wrapper for geocoding operations with provider metadata
/// </summary>
/// <typeparam name="T">Type of the result data</typeparam>
public sealed record GeocodeResult<T>
{
    /// <summary>
    /// The geocoding result data
    /// </summary>
    public required T Data { get; init; }

    /// <summary>
    /// Name of the provider that produced the result
    /// </summary>
    public required string ProviderName { get; init; }

    /// <summary>
    /// Whether the operation was successful
    /// </summary>
    public bool IsSuccess { get; init; } = true;

    /// <summary>
    /// Error message if the operation failed
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Response time in milliseconds
    /// </summary>
    public double? ResponseTimeMs { get; init; }

    /// <summary>
    /// List of providers that were attempted (for failover scenarios)
    /// </summary>
    public IReadOnlyList<string>? AttemptedProviders { get; init; }

    /// <summary>
    /// Additional metadata about the operation
    /// </summary>
    public IReadOnlyDictionary<string, object>? Metadata { get; init; }

}

/// <summary>
/// Helpers for constructing geocoding results
/// </summary>
public static class GeocodeResults
{
    /// <summary>
    /// Create a successful result
    /// </summary>
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

    /// <summary>
    /// Create a failed result
    /// </summary>
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
