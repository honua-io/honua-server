// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Honua.Core.Features.Shared.Models;

public sealed partial record CoordinateSystemEntry
{
    public required int Code { get; init; }
    public required string Name { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Unit { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Area { get; init; }

    public static CoordinateSystemEntry? FromDefinition(CrsDefinition definition)
        => FromWkt(definition.Srid, definition.Wkt);

    public static CoordinateSystemEntry? FromWkt(int code, string? wkt)
    {
        if (string.IsNullOrWhiteSpace(wkt)) return null;
        var name = NamePattern().Match(wkt);
        if (!name.Success) return null;
        var units = UnitPattern().Matches(wkt);
        var area = AreaPattern().Match(wkt);
        return new CoordinateSystemEntry
        {
            Code = code,
            Name = name.Groups[1].Value,
            Unit = units.Count > 0 ? units[^1].Groups[1].Value : null,
            Area = area.Success ? area.Groups[1].Value : null
        };
    }

    [GeneratedRegex("^\\s*[A-Za-z0-9_]+\\s*\\[\\s*\"([^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex("(?:ANGLEUNIT|LENGTHUNIT|UNIT)\\s*\\[\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnitPattern();

    [GeneratedRegex("AREA\\s*\\[\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AreaPattern();

}

public sealed record CoordinateSystemSearchResult
{
    public required IReadOnlyList<CoordinateSystemEntry> Items { get; init; }
    public bool HasMore { get; init; }
}
