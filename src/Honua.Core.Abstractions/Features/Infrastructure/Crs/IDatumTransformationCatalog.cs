// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;

namespace Honua.Core.Features.Infrastructure.Crs;

/// <summary>
/// Resolves datum (geographic) transformations for a source-to-target reprojection,
/// matching ArcGIS' default geotransformation selection so reprojected geometry
/// lands within a documented tolerance of Esri output.
/// </summary>
/// <remarks>
/// The catalog is the single, auditable source of truth for which PROJ pipeline a
/// given <c>(fromSrid → toSrid)</c> reprojection should use. It is consulted by the
/// query/import pipeline; protocol adapters never select pipelines themselves.
/// </remarks>
public interface IDatumTransformationCatalog
{
    /// <summary>
    /// Resolves the Esri-default datum transformation for the supplied SRID pair.
    /// </summary>
    /// <param name="fromSrid">Source CRS SRID (the stored layer geometry's SRID).</param>
    /// <param name="toSrid">Target CRS SRID (the requested output SRID).</param>
    /// <param name="selection">
    /// When the method returns <see langword="true"/>, the resolved transformation.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when an Esri-default transformation is known for the
    /// pair. <see langword="false"/> when no curated default exists — callers must
    /// then either fall back to the PostGIS default pipeline (for identity/no-shift
    /// pairs) or fail explicitly rather than silently substitute.
    /// </returns>
    bool TryGetDefault(int fromSrid, int toSrid, [NotNullWhen(true)] out DatumTransformationSelection? selection);

    /// <summary>
    /// Resolves a transformation explicitly requested by a client via its Esri WKID.
    /// </summary>
    /// <param name="wkid">The Esri geotransformation WKID.</param>
    /// <param name="fromSrid">Source CRS SRID for direction/validation.</param>
    /// <param name="toSrid">Target CRS SRID for direction/validation.</param>
    /// <param name="selection">
    /// When the method returns <see langword="true"/>, the resolved transformation,
    /// oriented to carry the source → target direction.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the WKID is recognized and applicable to the pair
    /// (in either direction). <see langword="false"/> when the WKID is unknown or
    /// does not connect the two CRSs — the caller must surface an explicit error.
    /// </returns>
    bool TryGetByWkid(int wkid, int fromSrid, int toSrid, [NotNullWhen(true)] out DatumTransformationSelection? selection);
}
