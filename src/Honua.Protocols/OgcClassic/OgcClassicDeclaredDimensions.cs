// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Protocols.Ogc.Classic;

internal readonly record struct DeclaredWmsDimension
{
    public required string Name { get; init; }
    public required string Units { get; init; }
    public string? UnitSymbol { get; init; }
    public bool MultipleValues { get; init; }
    public bool NearestValue { get; init; }
    public string? Default { get; init; }
    public string? Current { get; init; }
    public required string Extent { get; init; }
}

internal readonly record struct DeclaredWmtsDimension
{
    public required string Identifier { get; init; }
    public required string[] Values { get; init; }
    public string? DefaultValue { get; init; }
    public bool SupportsCurrent { get; init; }
    public string? CurrentValue { get; init; }
}

// Reads the dimensions a deployer declares on a resource under extensions.wms and
// extensions.wmts. The JSON is read by hand because the server is published native AOT,
// and every reader treats missing or malformed metadata as "nothing declared": resource
// metadata is deployer-authored and must never fail a capabilities or map request.
internal static class OgcClassicDeclaredDimensions
{
    private const string WmsExtensionKey = "wms";
    private const string WmtsExtensionKey = "wmts";
    private const string DimensionsProperty = "dimensions";
    private const string TimeDimensionName = "time";

    internal static DeclaredWmsDimension[] ReadWmsDimensions(MetadataV2Object resource)
    {
        var dimensions = new List<DeclaredWmsDimension>();
        foreach (var entry in EnumerateDimensionEntries(resource, WmsExtensionKey))
        {
            if (ReadWmsDimension(entry) is { } dimension &&
                !dimensions.Exists(existing => NameEquals(existing.Name, dimension.Name)))
            {
                dimensions.Add(dimension);
            }
        }

        return [.. dimensions];
    }

    internal static bool HasWmsTimeDimension(MetadataV2Object resource)
    {
        foreach (var entry in EnumerateDimensionEntries(resource, WmsExtensionKey))
        {
            if (ReadWmsDimension(entry) is { } dimension && NameEquals(dimension.Name, TimeDimensionName))
            {
                return true;
            }
        }

        return false;
    }

    internal static DeclaredWmtsDimension[] ReadWmtsDimensions(MetadataV2Object resource)
    {
        var dimensions = new List<DeclaredWmtsDimension>();
        foreach (var entry in EnumerateDimensionEntries(resource, WmtsExtensionKey))
        {
            if (ReadWmtsDimension(entry) is { } dimension &&
                !dimensions.Exists(existing => NameEquals(existing.Identifier, dimension.Identifier)))
            {
                dimensions.Add(dimension);
            }
        }

        return [.. dimensions];
    }

    internal static bool IsWmtsQueryable(MetadataV2Object resource)
        => GetExtensionObject(resource, WmtsExtensionKey) is not { } extension ||
           !extension.TryGetProperty("queryable", out var queryable) ||
           queryable.ValueKind != JsonValueKind.False;

    private static JsonElement? GetExtensionObject(MetadataV2Object resource, string key)
        => resource.Extensions.TryGetValue(key, out var extension) && extension.ValueKind == JsonValueKind.Object
            ? extension
            : null;

    private static IEnumerable<JsonElement> EnumerateDimensionEntries(MetadataV2Object resource, string key)
    {
        if (GetExtensionObject(resource, key) is not { } extension ||
            !extension.TryGetProperty(DimensionsProperty, out var entries) ||
            entries.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return entries.EnumerateArray().Where(entry => entry.ValueKind == JsonValueKind.Object);
    }

    private static DeclaredWmsDimension? ReadWmsDimension(JsonElement entry)
    {
        if (ReadName(entry, "name") is not { } name)
        {
            return null;
        }

        return new DeclaredWmsDimension
        {
            Name = name,
            Units = ReadName(entry, "units") ?? string.Empty,
            UnitSymbol = ReadName(entry, "unitSymbol"),
            MultipleValues = ReadFlag(entry, "multipleValues"),
            NearestValue = ReadFlag(entry, "nearestValue"),
            Default = ReadText(entry, "default"),
            Current = ReadText(entry, "current"),
            Extent = ReadText(entry, "extent") ?? string.Empty
        };
    }

    private static DeclaredWmtsDimension? ReadWmtsDimension(JsonElement entry)
    {
        // "time" is reserved for the dimension the server computes from temporal fields.
        if (ReadName(entry, "identifier") is not { } identifier ||
            NameEquals(identifier, TimeDimensionName) ||
            !entry.TryGetProperty("values", out var valuesElement) ||
            valuesElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string[] values = [.. valuesElement.EnumerateArray().Select(ReadScalar).OfType<string>()];

        // A WMTS Dimension must list at least one Value; an empty list would also read as
        // "any value accepted" to the request validator, so such an entry is not declared.
        if (values.Length == 0)
        {
            return null;
        }

        return new DeclaredWmtsDimension
        {
            Identifier = identifier,
            Values = values,
            DefaultValue = ReadText(entry, "default"),
            SupportsCurrent = ReadFlag(entry, "supportsCurrent"),
            CurrentValue = ReadText(entry, "current")
        };
    }

    private static string? ReadName(JsonElement parent, string property)
        => parent.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String
            ? ReadScalar(element)
            : null;

    private static string? ReadText(JsonElement parent, string property)
        => parent.TryGetProperty(property, out var element) ? ReadScalar(element) : null;

    // Numbers are accepted for values so a deployer can write "values": [100, 200] without quoting.
    private static string? ReadScalar(JsonElement element)
    {
        var text = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            _ => null
        };

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static bool ReadFlag(JsonElement parent, string property)
        => parent.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.True;

    private static bool NameEquals(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
