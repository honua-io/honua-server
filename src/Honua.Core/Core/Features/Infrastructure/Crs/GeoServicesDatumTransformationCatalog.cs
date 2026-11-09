// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Infrastructure.Crs;

public sealed class GeoServicesDatumTransformationCatalog : IDatumTransformationCatalog
{
    private const string ResourceName =
        "Honua.Core.Features.Infrastructure.Crs.Resources.esri-default-datum-transformations.json";

    private readonly FrozenDictionary<(int From, int To), DatumTransformationSelection> _byPair;
    private readonly FrozenDictionary<(int From, int To), AreaCandidate[]> _byArea;
    private readonly FrozenDictionary<int, DatumTransformationSelection> _byWkid;

    public GeoServicesDatumTransformationCatalog()
    {
        var table = LoadTable();

        var byPair = new Dictionary<(int, int), DatumTransformationSelection>();
        var byArea = new Dictionary<(int, int), List<AreaCandidate>>();
        var byWkid = new Dictionary<int, DatumTransformationSelection>();

        foreach (var entry in table)
        {
            var forward = ToSelection(entry, forward: true);

            // The first entry listed for a pair is its Esri default; later entries for the
            // same pair are area alternatives, chosen only when an envelope picks exactly one.
            byPair.TryAdd((entry.FromSrid, entry.ToSrid), forward);
            if (entry.Wkid is { } wkid)
            {
                byWkid[wkid] = forward;
            }

            // Synthesize the inverse direction so toSrid -> fromSrid resolves the same
            // authoritative pipeline applied in reverse.
            var inverse = ToSelection(entry, forward: false);
            byPair.TryAdd((entry.ToSrid, entry.FromSrid), inverse);

            if (entry.AreaWest is double areaWest
                && entry.AreaSouth is double areaSouth
                && entry.AreaEast is double areaEast
                && entry.AreaNorth is double areaNorth)
            {
                AddArea(byArea, (entry.FromSrid, entry.ToSrid), forward, areaWest, areaSouth, areaEast, areaNorth);
                AddArea(byArea, (entry.ToSrid, entry.FromSrid), inverse, areaWest, areaSouth, areaEast, areaNorth);
            }
        }

        _byPair = byPair.ToFrozenDictionary();
        _byArea = byArea.ToFrozenDictionary(
            pair => pair.Key,
            pair => pair.Value.ToArray());
        _byWkid = byWkid.ToFrozenDictionary();
    }

    public bool TryGetForEnvelope(
        int fromSrid,
        int toSrid,
        double west,
        double south,
        double east,
        double north,
        [NotNullWhen(true)] out DatumTransformationSelection? selection)
    {
        selection = null;
        if (!_byArea.TryGetValue((fromSrid, toSrid), out var candidates))
        {
            return false;
        }

        foreach (var candidate in candidates)
        {
            if (!Contains(candidate, west, south, east, north))
            {
                continue;
            }

            if (selection is not null)
            {
                selection = null;
                return false;
            }

            selection = candidate.Selection;
        }

        return selection is not null;
    }

    public bool HasAreaOfUse(int fromSrid, int toSrid)
        => _byArea.ContainsKey((fromSrid, toSrid));

    private static void AddArea(
        Dictionary<(int, int), List<AreaCandidate>> byArea,
        (int From, int To) pair,
        DatumTransformationSelection selection,
        double west,
        double south,
        double east,
        double north)
    {
        if (!byArea.TryGetValue(pair, out var list))
        {
            list = [];
            byArea[pair] = list;
        }

        list.Add(new AreaCandidate(selection, west, south, east, north));
    }

    private static bool Contains(AreaCandidate area, double west, double south, double east, double north)
    {
        if (south < area.South || north > area.North || south > north)
        {
            return false;
        }

        if (area.West <= area.East)
        {
            return west <= east && west >= area.West && east <= area.East;
        }

        return LongitudeInside(west, area.West, area.East)
            && LongitudeInside(east, area.West, area.East);
    }

    private static bool LongitudeInside(double longitude, double areaWest, double areaEast)
        => longitude >= areaWest || longitude <= areaEast;

    public bool TryGetDefault(int fromSrid, int toSrid, [NotNullWhen(true)] out DatumTransformationSelection? selection)
    {
        if (_byPair.TryGetValue((fromSrid, toSrid), out var found))
        {
            selection = found;
            return true;
        }

        selection = null;
        return false;
    }

    public bool TryGetByWkid(int wkid, int fromSrid, int toSrid, [NotNullWhen(true)] out DatumTransformationSelection? selection)
    {
        if (!_byWkid.TryGetValue(wkid, out var found))
        {
            selection = null;
            return false;
        }

        // Forward direction matches the requested pair as-is.
        if (found.FromSrid == fromSrid && found.ToSrid == toSrid)
        {
            selection = found;
            return true;
        }

        // The same WKID also covers the inverse direction; orient it to the request.
        if (found.FromSrid == toSrid && found.ToSrid == fromSrid)
        {
            selection = found with
            {
                FromSrid = fromSrid,
                ToSrid = toSrid,
                TransformForward = !found.TransformForward
            };
            return true;
        }

        // Known WKID but it does not connect the requested CRSs.
        selection = null;
        return false;
    }

    private static DatumTransformationSelection ToSelection(DatumTransformationEntry entry, bool forward)
        => new()
        {
            Wkid = entry.Wkid,
            Name = entry.Name,
            FromSrid = forward ? entry.FromSrid : entry.ToSrid,
            ToSrid = forward ? entry.ToSrid : entry.FromSrid,
            EpsgOperationCode = entry.EpsgOperationCode,
            ProjPipeline = entry.ProjPipeline,
            TransformForward = forward,
            RequiredGrids = entry.RequiredGrids ?? []
        };

    private static IReadOnlyList<DatumTransformationEntry> LoadTable()
    {
        var assembly = typeof(GeoServicesDatumTransformationCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded datum-transformation table '{ResourceName}' was not found.");

        var table = JsonSerializer.Deserialize(stream, DatumTransformationTableJsonContext.Default.DatumTransformationTable)
            ?? throw new InvalidOperationException("Failed to deserialize the datum-transformation table.");

        return table.Transformations ?? [];
    }

    public static IDatumTransformationCatalog Create() => new GeoServicesDatumTransformationCatalog();

    private readonly record struct AreaCandidate(
        DatumTransformationSelection Selection,
        double West,
        double South,
        double East,
        double North);
}

internal sealed record DatumTransformationTable
{
    public IReadOnlyList<DatumTransformationEntry>? Transformations { get; init; }
}

internal sealed record DatumTransformationEntry
{
    public int? Wkid { get; init; }

    public string Name { get; init; } = string.Empty;

    public int FromSrid { get; init; }

    public int ToSrid { get; init; }

    public string? AreaOfUse { get; init; }

    public double? AreaWest { get; init; }

    public double? AreaSouth { get; init; }

    public double? AreaEast { get; init; }

    public double? AreaNorth { get; init; }

    public int? EpsgOperationCode { get; init; }

    public string? ProjPipeline { get; init; }

    public IReadOnlyList<string>? RequiredGrids { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DatumTransformationTable))]
internal sealed partial class DatumTransformationTableJsonContext : JsonSerializerContext
{
}
