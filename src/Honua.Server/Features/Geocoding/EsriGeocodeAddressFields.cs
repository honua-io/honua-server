// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Geocoding.Features.Geocoding.Domain;

namespace Honua.Server.Features.Geocoding;

/// <summary>
/// Projects a provider result onto the Esri GeocodeServer address fields arcpy maps.
/// Provider-native keys are kept; missing Esri fields are filled from the structured address.
/// </summary>
/// <remarks>
/// Every documented field is emitted on every candidate, empty when the provider cannot
/// supply it. A real Esri locator returns the whole set with blanks rather than a subset,
/// and the tools read the schema from the first candidate: omitting a component entirely
/// is not the same statement as reporting it unknown. With only eleven of the twenty
/// present, <c>arcpy.geocoding.GeocodeAddresses</c> fails "ERROR 000010: Geocode addresses
/// failed" against a locator it has already bound and described as an AddressLocator
/// (honua-server#5145).
/// </remarks>
internal static class EsriGeocodeAddressFields
{
    /// <summary>
    /// Projects a candidate, stamping the output-schema fields the geocoding tools read
    /// off the result rather than compute.
    /// </summary>
    /// <remarks>
    /// <c>Loc_name</c>, <c>Status</c> and <c>Score</c> belong to a CANDIDATE, not to a
    /// reverse-geocode address: the live World locator returns all three from
    /// findAddressCandidates and none of them from reverseGeocode, so only this overload
    /// adds them. <c>Status</c> is derived rather than asserted - the batch path knows
    /// whether a record produced a location, and findAddressCandidates only ever returns
    /// candidates it matched, so "U" is reached exactly by a no-match slot.
    /// </remarks>
    internal static IReadOnlyDictionary<string, GeocodeAttributeValue> ApplyCandidate(
        string? matchedAddress,
        string? addressType,
        StructuredAddress? structured,
        IReadOnlyDictionary<string, string?> providerAttributes,
        double score,
        string? locatorName,
        bool isMatch)
    {
        var components = Apply(matchedAddress, addressType, structured, providerAttributes);
        var attributes = new Dictionary<string, GeocodeAttributeValue>(components.Count + 4, StringComparer.Ordinal);
        foreach (var pair in components)
        {
            attributes[pair.Key] = GeocodeAttributeValue.FromText(pair.Value);
        }

        attributes["Loc_name"] = GeocodeAttributeValue.FromText(
            string.IsNullOrWhiteSpace(locatorName) ? "Honua" : locatorName);
        attributes["Status"] = GeocodeAttributeValue.FromText(isMatch ? "M" : "U");

        // Numeric, because candidateFields declares Score as esriFieldTypeDouble and the
        // tools create a typed column from that. The live World locator agrees.
        attributes["Score"] = GeocodeAttributeValue.FromNumber(score);

        // Declared in candidateFields, so it has to be answerable on every candidate.
        if (!attributes.TryGetValue("Provider", out var provider) || string.IsNullOrWhiteSpace(provider.Text))
        {
            attributes["Provider"] = GeocodeAttributeValue.FromText(locatorName ?? string.Empty);
        }

        return attributes;
    }

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
        Set(attributes, "PlaceName", structured?.Subaddress);

        // Fill the remainder of the documented set with empty values so the schema is
        // complete. These are components the configured provider does not report; an
        // empty string says "unknown", while an absent key says "this locator has no
        // such field", which is what broke the tools.
        foreach (var field in DocumentedFields)
        {
            if (!attributes.ContainsKey(field))
            {
                attributes[field] = string.Empty;
            }
        }

        return attributes;
    }

    /// <summary>
    /// The address fields an Esri locator is expected to carry on every candidate.
    /// </summary>
    private static readonly string[] DocumentedFields =
    [
        "AddNum", "Addr_type", "Address", "Block", "City", "CountryCode", "District",
        "LongLabel", "Match_addr", "MetroArea", "Neighborhood", "PlaceName", "Postal",
        "PostalExt", "Region", "Sector", "ShortLabel", "Subregion", "Territory", "Type",
    ];

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
