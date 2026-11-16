// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Routing.Features.Routing.Domain;

public static class RoutingRequestValidation
{
    public static string? ValidateCapabilities(
        RoutingProviderCapabilities capabilities,
        IReadOnlyList<RouteBarrier> barriers,
        string? travelMode)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(barriers);

        foreach (var kind in barriers.Select(b => b.Kind).Distinct())
        {
            if (!capabilities.SupportedBarrierKinds.Contains(kind))
            {
                return $"{kind} barriers are not supported by the configured routing provider.";
            }
        }

        if (!string.IsNullOrWhiteSpace(travelMode) &&
            !capabilities.SupportedTravelModes.Any(m => string.Equals(m, travelMode, StringComparison.OrdinalIgnoreCase)))
        {
            return $"travelMode '{travelMode}' is not supported by the configured routing provider.";
        }

        return null;
    }
}
