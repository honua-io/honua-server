// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Geocoding.Features.Geocoding.Domain;

namespace Honua.Geocoding.Features.Geocoding.Abstractions;

public interface IGeocodeProvider
{
    string Name { get; }

    GeocodeProviderCapabilities Capabilities { get; }

    Task<IReadOnlyList<GeocodeCandidate>> ForwardGeocodeAsync(
        ForwardGeocodeRequest request,
        CancellationToken cancellationToken = default);

    Task<ReverseGeocodeMatch?> ReverseGeocodeAsync(
        ReverseGeocodeRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GeocodeSuggestion>> SuggestAsync(
        SuggestGeocodeRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GeocodeCandidate>> BatchGeocodeAsync(
        BatchGeocodeRequest request,
        CancellationToken cancellationToken = default);

    Task<GeocodeProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default);
}

public interface IGeocodeProviderFactory
{
    IGeocodeProvider? CreateProvider(string providerName);

    IReadOnlyList<string> GetAvailableProviders();

    IGeocodeProvider GetDefaultProvider();
}

public interface IGeocodeProviderCoordinator
{
    IGeocodeProvider? GetProvider(string? providerName = null);

    IReadOnlyList<IGeocodeProvider> GetAllProviders();

    Task<IReadOnlyList<GeocodeCandidate>> ForwardGeocodeAsync(
        ForwardGeocodeRequest request,
        string? providerName = null,
        CancellationToken cancellationToken = default);

    Task<ReverseGeocodeMatch?> ReverseGeocodeAsync(
        ReverseGeocodeRequest request,
        string? providerName = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GeocodeProviderHealth>> CheckAllProvidersHealthAsync(CancellationToken cancellationToken = default);
}
