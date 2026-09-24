// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Infrastructure.Crs;

/// <summary>
/// Longitude/latitude envelope used to choose a datum grid by area of use.
/// </summary>
/// <param name="West">West longitude, degrees.</param>
/// <param name="South">South latitude, degrees.</param>
/// <param name="East">East longitude, degrees.</param>
/// <param name="North">North latitude, degrees.</param>
public readonly record struct DatumAreaEnvelope(double West, double South, double East, double North);

/// <summary>
/// <see cref="IDatumTransformationCatalog"/> backed by an embedded, auditable table of
/// Esri default geotransformations. Models the Esri-parity selections as data so the
/// table can be reviewed and extended without code changes.
/// </summary>
/// <remarks>
/// Reverse directions are synthesized at load time: an entry for
/// <c>(fromSrid → toSrid)</c> also resolves <c>(toSrid → fromSrid)</c> with
/// <see cref="DatumTransformationSelection.TransformForward"/> set to
/// <see langword="false"/> and source/target swapped, so the runtime applies the
/// inverse of the same authoritative pipeline.
/// </remarks>
public sealed class EsriDatumTransformationCatalog : IDatumTransformationCatalog
{
    private const string ResourceName =
        "Honua.Core.Features.Infrastructure.Crs.Resources.esri-default-datum-transformations.json";

    private readonly FrozenDictionary<(int From, int To), DatumTransformationSelection> _byPair;
    private readonly FrozenDictionary<(int From, int To), AreaCandidate[]> _byArea;
    private readonly FrozenDictionary<int, DatumTransformationSelection> _byWkid;

    /// <summary>
    /// Initializes the catalog from the embedded Esri-default transformation table.
    /// </summary>
    public EsriDatumTransformationCatalog()
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

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
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
        var assembly = typeof(EsriDatumTransformationCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded datum-transformation table '{ResourceName}' was not found.");

        var table = JsonSerializer.Deserialize(stream, DatumTransformationTableJsonContext.Default.DatumTransformationTable)
            ?? throw new InvalidOperationException("Failed to deserialize the datum-transformation table.");

        return table.Transformations ?? [];
    }

    /// <summary>
    /// Loads a fresh catalog instance from the embedded table.
    /// </summary>
    /// <returns>A new <see cref="IDatumTransformationCatalog"/>.</returns>
    public static IDatumTransformationCatalog Create() => new EsriDatumTransformationCatalog();

    private readonly record struct AreaCandidate(
        DatumTransformationSelection Selection,
        double West,
        double South,
        double East,
        double North);
}

/// <summary>
/// Root of the embedded Esri-default transformation table.
/// </summary>
internal sealed record DatumTransformationTable
{
    /// <summary>Transformation entries.</summary>
    public IReadOnlyList<DatumTransformationEntry>? Transformations { get; init; }
}

/// <summary>
/// A single row of the embedded Esri-default transformation table.
/// </summary>
internal sealed record DatumTransformationEntry
{
    /// <summary>Esri geotransformation WKID.</summary>
    public int? Wkid { get; init; }

    /// <summary>Transformation name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Source CRS SRID.</summary>
    public int FromSrid { get; init; }

    /// <summary>Target CRS SRID.</summary>
    public int ToSrid { get; init; }

    /// <summary>Human-readable area of use (provenance only).</summary>
    public string? AreaOfUse { get; init; }

    /// <summary>West bound of the area of use, degrees. With the other bounds, enables envelope selection.</summary>
    public double? AreaWest { get; init; }

    /// <summary>South bound of the area of use, degrees.</summary>
    public double? AreaSouth { get; init; }

    /// <summary>East bound of the area of use, degrees. Less than <see cref="AreaWest"/> when the area crosses the antimeridian.</summary>
    public double? AreaEast { get; init; }

    /// <summary>North bound of the area of use, degrees.</summary>
    public double? AreaNorth { get; init; }

    /// <summary>EPSG coordinate-operation code.</summary>
    public int? EpsgOperationCode { get; init; }

    /// <summary>Explicit PROJ pipeline string for 3-argument ST_Transform.</summary>
    public string? ProjPipeline { get; init; }

    /// <summary>PROJ grid-shift files the pipeline depends on.</summary>
    public IReadOnlyList<string>? RequiredGrids { get; init; }
}

/// <summary>
/// Source-generated JSON context for the embedded datum-transformation table.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DatumTransformationTable))]
internal sealed partial class DatumTransformationTableJsonContext : JsonSerializerContext
{
}
