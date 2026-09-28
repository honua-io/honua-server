// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Geocoding.Features.Geocoding.Domain;

namespace Honua.Protocols.GeoServices.GeocodeServer;

/// <summary>Shared metadata and catalog projection for GeoServices locator capabilities.</summary>
internal static class GeocodeServerCapabilities
{
    public static GeocodeProviderCapabilities ApplyLicense(
        GeocodeProviderCapabilities capabilities, ILicenseEntitlementService entitlements)
        => entitlements.CheckEntitlement(FeatureCatalog.BatchGeocodingKey).IsActive
            ? capabilities
            : capabilities with { SupportsBatch = false };

    public static string Format(GeocodeProviderCapabilities capabilities)
    {
        var availableCapabilities = new List<string>(capacity: 4)
        {
            "Geocode",
            "ReverseGeocode"
        };

        if (capabilities.SupportsSuggest)
        {
            availableCapabilities.Add("Suggest");
        }

        if (capabilities.SupportsBatch)
        {
            availableCapabilities.Add("BatchGeocode");
        }

        return string.Join(',', availableCapabilities);
    }
}
