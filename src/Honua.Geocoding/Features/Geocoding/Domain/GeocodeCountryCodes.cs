// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Geocoding.Features.Geocoding.Domain;

public static class GeocodeCountryCodes
{
    private static readonly Lazy<CountryAliases> Aliases = new(CreateAliases);

    public const string InvalidMessage = "Invalid countryCodes: expected comma-separated two-letter ISO country codes.";

    public static bool TryParse(string? value, out string[]? codes)
    {
        codes = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        // Every ISO alpha-2 country fits well inside this bound. Check before
        // splitting so a hostile request cannot allocate an unbounded array.
        if (value.Length > 1024)
        {
            return false;
        }

        var requested = value.Split(',', StringSplitOptions.TrimEntries);
        if (requested.Length > 250 || requested.Any(static code =>
                code.Length != 2 || !char.IsAsciiLetter(code[0]) || !char.IsAsciiLetter(code[1])))
        {
            return false;
        }

        codes = requested.Select(static code => code.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return true;
    }

    public static bool TryParseGeoServicesCountry(string? value, out string? code)
    {
        code = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var token = value.Trim();
        if (token.Length > 128)
        {
            return false;
        }

        if (token.Length == 2 && char.IsAsciiLetter(token[0]) && char.IsAsciiLetter(token[1]))
        {
            code = token.ToUpperInvariant();
            return true;
        }

        return Aliases.Value.UniqueCodeByAlias.TryGetValue(token, out code);
    }

    public static string[] StoredRepresentations(IEnumerable<string> codes)
    {
        var aliases = Aliases.Value.AliasesByCode;
        return codes.SelectMany(code => aliases.TryGetValue(code, out var names) ? names : [code])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public static bool Matches(string? value, IEnumerable<string> codes)
        => value is not null && StoredRepresentations(codes)
            .Contains(value.Trim().ToUpperInvariant(), StringComparer.Ordinal);

    private static CountryAliases CreateAliases()
    {
        var byAlias = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            var region = new RegionInfo(culture.Name);
            var code = region.TwoLetterISORegionName;
            if (code.Length != 2 || !char.IsAsciiLetter(code[0]) || !char.IsAsciiLetter(code[1]))
            {
                continue;
            }

            foreach (var value in new[] { code, region.ThreeLetterISORegionName, region.EnglishName, region.NativeName })
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var alias = value.Trim().ToUpperInvariant();
                if (!byAlias.TryGetValue(alias, out var mapped))
                {
                    mapped = new HashSet<string>(StringComparer.Ordinal);
                    byAlias.Add(alias, mapped);
                }

                mapped.Add(code);
            }
        }

        var unique = byAlias.Where(static pair => pair.Value.Count == 1)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value.Single(), StringComparer.OrdinalIgnoreCase);
        var byCode = unique.GroupBy(static pair => pair.Value, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Select(static pair => pair.Key).ToArray(),
                StringComparer.Ordinal);
        return new CountryAliases(unique, byCode);
    }

    private sealed record CountryAliases(
        IReadOnlyDictionary<string, string> UniqueCodeByAlias,
        IReadOnlyDictionary<string, string[]> AliasesByCode);
}
