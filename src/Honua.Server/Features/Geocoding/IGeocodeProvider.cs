// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Geocoding;

internal interface IGeocodeProvider
{
    string Name { get; }

    GeocodeProviderCapabilities Capabilities { get; }

    Task<IReadOnlyList<GeocodeCandidate>> ForwardGeocodeAsync(ForwardGeocodeRequest request, CancellationToken cancellationToken);

    Task<ReverseGeocodeMatch?> ReverseGeocodeAsync(ReverseGeocodeRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<GeocodeSuggestion>> SuggestAsync(SuggestGeocodeRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<GeocodeCandidate>> BatchGeocodeAsync(BatchGeocodeRequest request, CancellationToken cancellationToken);
}
