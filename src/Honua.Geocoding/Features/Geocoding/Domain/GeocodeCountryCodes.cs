// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

    // The table is generated from ICU region data (ISO alpha-2, alpha-3, English
    // and native names). The postgis AOT image runs with invariant globalization,
    // where culture enumeration yields only the invariant culture and RegionInfo
    // cannot be built, so the aliases ship with the assembly instead.
    private const string AliasResource = "Honua.Geocoding.country-aliases.tsv";

    private static CountryAliases CreateAliases()
    {
        var byAlias = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (code, names) in ReadAliasTable())
        {
            foreach (var alias in names.Prepend(code))
            {
                AddAlias(byAlias, alias, code);
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

    private static void AddAlias(Dictionary<string, HashSet<string>> byAlias, string alias, string code)
    {
        if (!byAlias.TryGetValue(alias, out var mapped))
        {
            mapped = new HashSet<string>(StringComparer.Ordinal);
            byAlias.Add(alias, mapped);
        }

        mapped.Add(code);
    }

    private static List<(string Code, string[] Names)> ReadAliasTable()
    {
        using var stream = typeof(GeocodeCountryCodes).Assembly.GetManifestResourceStream(AliasResource)
            ?? throw new InvalidOperationException($"Embedded resource '{AliasResource}' is missing.");
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        var rows = new List<(string Code, string[] Names)>();
        while (reader.ReadLine() is { } line)
        {
            var fields = line.Split('\t', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length > 0 && fields[0].Length == 2)
            {
                rows.Add((fields[0], fields[1..]));
            }
        }

        return rows;
    }

    private sealed record CountryAliases(
        IReadOnlyDictionary<string, string> UniqueCodeByAlias,
        IReadOnlyDictionary<string, string[]> AliasesByCode);
}
