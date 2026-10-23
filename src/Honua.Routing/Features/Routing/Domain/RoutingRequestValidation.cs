// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Routing.Features.Routing.Domain;

/// <summary>Validates canonical routing inputs against the provider that will solve them.</summary>
public static class RoutingRequestValidation
{
    /// <summary>
    /// Returns an error for an unsupported barrier kind or travel mode, or null when
    /// the provider supports the inputs. An absent travel mode uses the provider default.
    /// </summary>
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
