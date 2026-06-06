// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Infrastructure.Crs;

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
    private readonly FrozenDictionary<int, DatumTransformationSelection> _byWkid;

    /// <summary>
    /// Initializes the catalog from the embedded Esri-default transformation table.
    /// </summary>
    public EsriDatumTransformationCatalog()
    {
        var table = LoadTable();

        var byPair = new Dictionary<(int, int), DatumTransformationSelection>();
        var byWkid = new Dictionary<int, DatumTransformationSelection>();

        foreach (var entry in table)
        {
            var forward = ToSelection(entry, forward: true);

            // The first entry listed for a pair is its Esri default; later entries for the
            // same pair (alternative transformations) remain resolvable only by WKID.
            byPair.TryAdd((entry.FromSrid, entry.ToSrid), forward);
            if (entry.Wkid is { } wkid)
            {
                byWkid[wkid] = forward;
            }

            // Synthesize the inverse direction so toSrid -> fromSrid resolves the same
            // authoritative pipeline applied in reverse.
            var inverse = ToSelection(entry, forward: false);
            byPair.TryAdd((entry.ToSrid, entry.FromSrid), inverse);
        }

        _byPair = byPair.ToFrozenDictionary();
        _byWkid = byWkid.ToFrozenDictionary();
    }

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
