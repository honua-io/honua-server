// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Honua.Core.Features.Tiles;

/// <summary>
/// The single source of truth for the tile matrix sets (gridsets) the server advertises: the two
/// built-in gridsets (WebMercatorQuad, WorldCRS84Quad) merged with any operator-defined custom
/// gridsets bound from configuration. Both the OGC API Tiles and classic WMTS adapters resolve
/// supported gridsets and their <see cref="GridGeometry"/> through this registry instead of
/// hardcoding the supported set per protocol.
/// </summary>
public interface ITileMatrixSetRegistry
{
    /// <summary>
    /// All registered tile matrix sets (built-in first, then operator-defined in configured
    /// order), as metadata-only entries.
    /// </summary>
    IReadOnlyList<TileMatrixSetEntry> All { get; }

    /// <summary>
    /// Determines whether a tile matrix set with the given identifier is registered
    /// (case-insensitive).
    /// </summary>
    /// <param name="tileMatrixSetId">The tile matrix set identifier.</param>
    bool IsSupported(string tileMatrixSetId);

    /// <summary>
    /// Looks up a tile matrix set metadata entry by identifier (case-insensitive).
    /// </summary>
    /// <param name="tileMatrixSetId">The tile matrix set identifier.</param>
    /// <param name="entry">The resolved entry when found.</param>
    /// <returns><see langword="true"/> when an entry was found.</returns>
    bool TryGet(string tileMatrixSetId, [NotNullWhen(true)] out TileMatrixSetEntry? entry);

    /// <summary>
    /// Resolves the full <see cref="GridGeometry"/> (per-level matrices) for a tile matrix set.
    /// For the built-in gridsets the levels are generated for <c>0..maxLevel</c> using the
    /// canonical OGC formulas so output stays byte-identical; for custom gridsets the configured
    /// levels are returned (a <paramref name="maxLevel"/> argument is ignored).
    /// </summary>
    /// <param name="tileMatrixSetId">The tile matrix set identifier.</param>
    /// <param name="maxLevel">The maximum level to generate for built-in (formula-driven) gridsets.</param>
    /// <param name="geometry">The resolved grid geometry when found.</param>
    /// <returns><see langword="true"/> when the gridset is registered.</returns>
    bool TryGetGeometry(string tileMatrixSetId, int maxLevel, [NotNullWhen(true)] out GridGeometry? geometry);
}

/// <summary>
/// Metadata describing a registered tile matrix set (gridset) without the per-level matrices.
/// </summary>
public sealed record TileMatrixSetEntry
{
    /// <summary>The tile matrix set identifier.</summary>
    public required string Id { get; init; }

    /// <summary>The human-readable title.</summary>
    public required string Title { get; init; }

    /// <summary>The tile matrix set URI.</summary>
    public required string Uri { get; init; }

    /// <summary>The CRS URI advertised for this gridset.</summary>
    public required string Crs { get; init; }

    /// <summary>The numeric SRID of the gridset CRS.</summary>
    public required int Srid { get; init; }

    /// <summary><see langword="true"/> when the gridset CRS is geographic (degrees) rather than projected.</summary>
    public required bool IsGeographic { get; init; }

    /// <summary>
    /// <see langword="true"/> for the two reserved built-in gridsets, <see langword="false"/>
    /// for operator-defined custom gridsets.
    /// </summary>
    public required bool IsBuiltIn { get; init; }
}
