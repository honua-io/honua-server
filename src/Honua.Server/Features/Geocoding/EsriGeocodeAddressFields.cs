// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Geocoding.Features.Geocoding.Domain;

namespace Honua.Server.Features.Geocoding;

/// <summary>
/// Projects a provider result onto the Esri GeocodeServer address fields arcpy maps.
/// Provider-native keys are kept; missing Esri fields are filled from the structured address.
/// </summary>
internal static class EsriGeocodeAddressFields
{
    internal static IReadOnlyDictionary<string, string?> Apply(
        string? matchedAddress,
        string? addressType,
        StructuredAddress? structured,
        IReadOnlyDictionary<string, string?> providerAttributes)
    {
        var attributes = new Dictionary<string, string?>(providerAttributes, StringComparer.Ordinal);
        var street = Join(structured?.AddressNumber, structured?.StreetName);
        Set(attributes, "Match_addr", matchedAddress);
        Set(attributes, "LongLabel", matchedAddress);
        Set(attributes, "ShortLabel", street ?? matchedAddress);
        Set(attributes, "Address", street ?? matchedAddress);
        Set(attributes, "AddNum", structured?.AddressNumber);
        Set(attributes, "Addr_type", addressType);
        Set(attributes, "Type", addressType);
        Set(attributes, "City", structured?.City);
        Set(attributes, "Region", structured?.Region);
        Set(attributes, "Neighborhood", structured?.Neighborhood);
        Set(attributes, "Postal", structured?.PostalCode);
        Set(attributes, "CountryCode", structured?.CountryCode);
        return attributes;
    }

    private static void Set(Dictionary<string, string?> attributes, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (attributes.TryGetValue(key, out var existing) && !string.IsNullOrWhiteSpace(existing))
        {
            return;
        }

        attributes[key] = value;
    }

    private static string? Join(string? number, string? street)
    {
        if (string.IsNullOrWhiteSpace(number))
        {
            return string.IsNullOrWhiteSpace(street) ? null : street.Trim();
        }

        if (string.IsNullOrWhiteSpace(street))
        {
            return number.Trim();
        }

        return $"{number.Trim()} {street.Trim()}";
    }
}
