// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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
