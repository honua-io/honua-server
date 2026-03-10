// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geocoding.Domain;

namespace Honua.Core.Features.Geocoding.Abstractions;

/// <summary>
/// Core abstraction for geocoding providers
/// </summary>
public interface IGeocodeProvider
{
    /// <summary>
    /// Unique name of the provider
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Provider capabilities and limitations
    /// </summary>
    GeocodeProviderCapabilities Capabilities { get; }

    /// <summary>
    /// Perform forward geocoding (address to coordinates)
    /// </summary>
    /// <param name="request">Forward geocoding request</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of geocoding candidates</returns>
    Task<IReadOnlyList<GeocodeCandidate>> ForwardGeocodeAsync(
        ForwardGeocodeRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Perform reverse geocoding (coordinates to address)
    /// </summary>
    /// <param name="request">Reverse geocoding request</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Reverse geocoding match or null if no match found</returns>
    Task<ReverseGeocodeMatch?> ReverseGeocodeAsync(
        ReverseGeocodeRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get geocoding suggestions for autocomplete
    /// </summary>
    /// <param name="request">Suggest request</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of geocoding suggestions</returns>
    Task<IReadOnlyList<GeocodeSuggestion>> SuggestAsync(
        SuggestGeocodeRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Perform batch geocoding for multiple addresses
    /// </summary>
    /// <param name="request">Batch geocoding request</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of geocoding candidates</returns>
    Task<IReadOnlyList<GeocodeCandidate>> BatchGeocodeAsync(
        BatchGeocodeRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Check the health status of the provider
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Provider health status</returns>
    Task<GeocodeProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Factory for creating geocoding providers
/// </summary>
public interface IGeocodeProviderFactory
{
    /// <summary>
    /// Create a geocoding provider by name
    /// </summary>
    /// <param name="providerName">Name of the provider</param>
    /// <returns>Geocoding provider instance</returns>
    IGeocodeProvider? CreateProvider(string providerName);

    /// <summary>
    /// Get all available provider names
    /// </summary>
    /// <returns>List of available provider names</returns>
    IReadOnlyList<string> GetAvailableProviders();

    /// <summary>
    /// Get the default provider
    /// </summary>
    /// <returns>Default geocoding provider</returns>
    IGeocodeProvider GetDefaultProvider();
}

/// <summary>
/// Service for coordinating multiple geocoding providers
/// </summary>
public interface IGeocodeProviderCoordinator
{
    /// <summary>
    /// Get a provider by name
    /// </summary>
    /// <param name="providerName">Name of the provider</param>
    /// <returns>Geocoding provider or null if not found</returns>
    IGeocodeProvider? GetProvider(string? providerName = null);

    /// <summary>
    /// Get all available providers
    /// </summary>
    /// <returns>List of all configured providers</returns>
    IReadOnlyList<IGeocodeProvider> GetAllProviders();

    /// <summary>
    /// Perform forward geocoding with failover to backup providers
    /// </summary>
    /// <param name="request">Forward geocoding request</param>
    /// <param name="providerName">Preferred provider name (optional)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of geocoding candidates</returns>
    Task<IReadOnlyList<GeocodeCandidate>> ForwardGeocodeAsync(
        ForwardGeocodeRequest request,
        string? providerName = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Perform reverse geocoding with failover to backup providers
    /// </summary>
    /// <param name="request">Reverse geocoding request</param>
    /// <param name="providerName">Preferred provider name (optional)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Reverse geocoding match or null if no match found</returns>
    Task<ReverseGeocodeMatch?> ReverseGeocodeAsync(
        ReverseGeocodeRequest request,
        string? providerName = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get health status of all providers
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Health status of all providers</returns>
    Task<IReadOnlyList<GeocodeProviderHealth>> CheckAllProvidersHealthAsync(CancellationToken cancellationToken = default);
}