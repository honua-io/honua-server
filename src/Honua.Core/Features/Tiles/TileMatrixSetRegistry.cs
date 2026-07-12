// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Honua.Core.Features.Shared.Models;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.Tiles;

/// <summary>
/// Default <see cref="ITileMatrixSetRegistry"/> implementation. Seeds the two reserved built-in
/// gridsets (WebMercatorQuad, WorldCRS84Quad) from the canonical OGC constants and formulas so
/// their advertised geometry is byte-identical to the previous hardcoded paths, then merges any
/// operator-defined custom gridsets bound from the <c>TileMatrixSets</c> configuration section.
/// </summary>
public sealed class TileMatrixSetRegistry : ITileMatrixSetRegistry
{
    /// <summary>WebMercatorQuad reserved built-in identifier.</summary>
    public const string WebMercatorQuadId = "WebMercatorQuad";

    /// <summary>WorldCRS84Quad reserved built-in identifier.</summary>
    public const string WorldCrs84QuadId = "WorldCRS84Quad";

    private const string WebMercatorQuadUri = "http://www.opengis.net/def/tilematrixset/OGC/1.0/WebMercatorQuad";
    private const string WebMercatorCrs = "http://www.opengis.net/def/crs/EPSG/0/3857";
    private const string WebMercatorTitle = "Web Mercator Quad";

    private const string WorldCrs84QuadUri = "http://www.opengis.net/def/tilematrixset/OGC/1.0/WorldCRS84Quad";
    private const string Crs84 = "http://www.opengis.net/def/crs/OGC/1.3/CRS84";
    private const string WorldCrs84Title = "World CRS84 Quad";

    private const double WebMercatorExtent = SpatialConstants.WebMercatorExtent;
    private const double PixelSizeMeters = SpatialConstants.PixelSizeMeters;
    private const double DegreesPerPixel = SpatialConstants.DegreesPerPixel;
    private const int DefaultTileSize = 256;

    /// <summary>
    /// The reserved built-in identifiers that operators may not redefine. Exposed so the
    /// configuration validator can reject collisions with the same source of truth.
    /// </summary>
    public static readonly ImmutableArray<string> ReservedIds =
        [WebMercatorQuadId, WorldCrs84QuadId];

    private readonly ImmutableArray<TileMatrixSetEntry> _entries;
    private readonly Dictionary<string, TileMatrixSetEntry> _entriesById;
    private readonly Dictionary<string, CustomTileMatrixSet> _customById;

    // Built-in gridset geometry is a pure function of (id, maxLevel): WebMercatorQuad and
    // WorldCRS84Quad never change after construction, so the ImmutableArray<GridLevel>
    // rebuilt on every TryGetGeometry call (once per tile/capabilities request) is cached
    // here instead of being recomputed on every call.
    private readonly ConcurrentDictionary<(string Id, int MaxLevel), GridGeometry> _builtInGeometryCache = new();

    /// <summary>
    /// Creates a registry from the bound <see cref="TileMatrixSetDefinitionOptions"/>.
    /// </summary>
    /// <param name="options">The bound custom tile matrix set options.</param>
    public TileMatrixSetRegistry(IOptions<TileMatrixSetDefinitionOptions> options)
        : this(options?.Value ?? new TileMatrixSetDefinitionOptions())
    {
    }

    /// <summary>
    /// Creates a registry from the supplied options instance (used directly by tests).
    /// </summary>
    /// <param name="options">The custom tile matrix set options.</param>
    public TileMatrixSetRegistry(TileMatrixSetDefinitionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var entries = ImmutableArray.CreateBuilder<TileMatrixSetEntry>();
        entries.Add(new TileMatrixSetEntry
        {
            Id = WebMercatorQuadId,
            Title = WebMercatorTitle,
            Uri = WebMercatorQuadUri,
            Crs = WebMercatorCrs,
            Srid = 3857,
            IsGeographic = false,
            IsBuiltIn = true
        });
        entries.Add(new TileMatrixSetEntry
        {
            Id = WorldCrs84QuadId,
            Title = WorldCrs84Title,
            Uri = WorldCrs84QuadUri,
            Crs = Crs84,
            Srid = 4326,
            IsGeographic = true,
            IsBuiltIn = true
        });

        _customById = new Dictionary<string, CustomTileMatrixSet>(StringComparer.OrdinalIgnoreCase);
        foreach (var custom in options.Custom ?? new List<CustomTileMatrixSet>())
        {
            if (custom is null || string.IsNullOrWhiteSpace(custom.Id))
            {
                continue;
            }

            // Defensive: a misconfigured duplicate or reserved-id collision should have been
            // rejected at startup by TileMatrixSetOptionsValidator. Skip silently here so the
            // registry never throws while serving requests.
            if (_customById.ContainsKey(custom.Id) ||
                ReservedIds.Any(reserved => string.Equals(reserved, custom.Id, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            _customById[custom.Id] = custom;
            entries.Add(new TileMatrixSetEntry
            {
                Id = custom.Id,
                Title = string.IsNullOrWhiteSpace(custom.Title) ? custom.Id : custom.Title!,
                Uri = string.IsNullOrWhiteSpace(custom.Uri) ? custom.Id : custom.Uri!,
                Crs = custom.Crs,
                Srid = custom.Srid,
                IsGeographic = IsGeographicSrid(custom.Srid),
                IsBuiltIn = false
            });
        }

        _entries = entries.ToImmutable();
        _entriesById = _entries.ToDictionary(entry => entry.Id, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public IReadOnlyList<TileMatrixSetEntry> All => _entries;

    /// <inheritdoc />
    public bool IsSupported(string tileMatrixSetId)
        => !string.IsNullOrWhiteSpace(tileMatrixSetId) && _entriesById.ContainsKey(tileMatrixSetId);

    /// <inheritdoc />
    public bool TryGet(string tileMatrixSetId, [NotNullWhen(true)] out TileMatrixSetEntry? entry)
    {
        if (!string.IsNullOrWhiteSpace(tileMatrixSetId) &&
            _entriesById.TryGetValue(tileMatrixSetId, out var found))
        {
            entry = found;
            return true;
        }

        entry = null;
        return false;
    }

    /// <inheritdoc />
    public bool TryGetGeometry(string tileMatrixSetId, int maxLevel, [NotNullWhen(true)] out GridGeometry? geometry)
    {
        if (string.IsNullOrWhiteSpace(tileMatrixSetId) || !_entriesById.TryGetValue(tileMatrixSetId, out var entry))
        {
            geometry = null;
            return false;
        }

        if (entry.IsBuiltIn)
        {
            geometry = _builtInGeometryCache.GetOrAdd(
                (entry.Id, maxLevel),
                key => string.Equals(key.Id, WorldCrs84QuadId, StringComparison.OrdinalIgnoreCase)
                    ? BuildWorldCrs84QuadGeometry(key.MaxLevel)
                    : BuildWebMercatorQuadGeometry(key.MaxLevel));
            return true;
        }

        geometry = BuildCustomGeometry(entry, _customById[entry.Id]);
        return true;
    }

    private static GridGeometry BuildWebMercatorQuadGeometry(int maxLevel)
    {
        var levels = ImmutableArray.CreateBuilder<GridLevel>();
        for (var z = 0; z <= Math.Max(0, maxLevel); z++)
        {
            var matrixSize = 1L << z;
            var cellSize = (2.0 * WebMercatorExtent) / ((double)DefaultTileSize * matrixSize);
            var scaleDenominator = cellSize / PixelSizeMeters;
            levels.Add(new GridLevel(z, scaleDenominator, cellSize, matrixSize, matrixSize));
        }

        return new GridGeometry
        {
            Id = WebMercatorQuadId,
            Srid = 3857,
            IsGeographic = false,
            TopLeftX = -WebMercatorExtent,
            TopLeftY = WebMercatorExtent,
            TileWidth = DefaultTileSize,
            TileHeight = DefaultTileSize,
            Levels = levels.ToImmutable()
        };
    }

    private static GridGeometry BuildWorldCrs84QuadGeometry(int maxLevel)
    {
        var levels = ImmutableArray.CreateBuilder<GridLevel>();
        for (var z = 0; z <= Math.Max(0, maxLevel); z++)
        {
            // WorldCRS84Quad: at zoom 0, 2 cols x 1 row covering the full globe.
            var numRows = 1L << z;
            var numCols = numRows * 2;
            var cellSize = 180.0 / ((double)DefaultTileSize * numRows);
            var scaleDenominator = cellSize / DegreesPerPixel;
            levels.Add(new GridLevel(z, scaleDenominator, cellSize, numCols, numRows));
        }

        return new GridGeometry
        {
            Id = WorldCrs84QuadId,
            Srid = 4326,
            IsGeographic = true,
            TopLeftX = -180.0,
            TopLeftY = 90.0,
            TileWidth = DefaultTileSize,
            TileHeight = DefaultTileSize,
            Levels = levels.ToImmutable()
        };
    }

    private static GridGeometry BuildCustomGeometry(TileMatrixSetEntry entry, CustomTileMatrixSet custom)
    {
        var levels = ImmutableArray.CreateBuilder<GridLevel>(custom.Levels.Count);
        foreach (var level in custom.Levels.OrderBy(candidate => candidate.Id))
        {
            levels.Add(new GridLevel(
                level.Id,
                level.ScaleDenominator,
                level.CellSize,
                level.MatrixWidth,
                level.MatrixHeight));
        }

        var topLeftX = custom.TopLeftCorner.Length > 0 ? custom.TopLeftCorner[0] : 0.0;
        var topLeftY = custom.TopLeftCorner.Length > 1 ? custom.TopLeftCorner[1] : 0.0;

        return new GridGeometry
        {
            Id = entry.Id,
            Srid = custom.Srid,
            IsGeographic = entry.IsGeographic,
            TopLeftX = topLeftX,
            TopLeftY = topLeftY,
            TileWidth = custom.TileWidth,
            TileHeight = custom.TileHeight,
            Levels = levels.ToImmutable()
        };
    }

    private static bool IsGeographicSrid(int srid)
        => GeographicSridClassifier.IsGeographicSrid(srid);
}
