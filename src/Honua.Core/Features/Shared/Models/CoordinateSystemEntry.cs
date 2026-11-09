// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Shared.Models;

public sealed record CoordinateSystemEntry
{
    public required int Code { get; init; }
    public required string Name { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Unit { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Area { get; init; }

    public static CoordinateSystemEntry? FromDefinition(CrsDefinition definition)
    {
        var wkt = definition.Wkt;
        if (string.IsNullOrEmpty(wkt)) return null;
        var start = wkt.IndexOf('"');
        var end = start < 0 ? -1 : wkt.IndexOf('"', start + 1);
        return end > start
            ? new CoordinateSystemEntry { Code = definition.Srid, Name = wkt[(start + 1)..end] }
            : null;
    }
}

public sealed record CoordinateSystemSearchResult
{
    public required IReadOnlyList<CoordinateSystemEntry> Items { get; init; }
    public bool HasMore { get; init; }
}
