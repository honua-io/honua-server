// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Scene.Domain;

namespace Honua.Core.Features.Scene.Abstractions;

/// <summary>
/// Source seam that streams the canonical features for a feature layer in
/// the shape required by the v1 3D Tiles generation pipeline (#842).
/// </summary>
/// <remarks>
/// Implementations are responsible for projecting the layer's geometry into
/// WGS-84 lon/lat (degrees) with optional ellipsoidal height, deduplicating
/// multi-part geometries into a single ordered ring per feature, and ordering
/// the result by primary key so the executor can produce deterministic
/// output. The Postgres implementation reads from PostGIS via
/// <c>ST_AsBinary(ST_Transform(geom, 4326))</c>; an in-memory implementation
/// is used by unit tests.
/// </remarks>
public interface ISceneFeatureSource
{
    /// <summary>
    /// Streams features for the supplied layer ordered by the layer's object id.
    /// </summary>
    /// <param name="layer">Layer definition with field schema and storage mapping.</param>
    /// <param name="includeAttributes">Allowlist of attribute field names; empty means include all numeric/string attributes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async sequence of <see cref="SceneFeature"/> values in deterministic order.</returns>
    IAsyncEnumerable<SceneFeature> StreamAsync(
        LayerDefinition layer,
        IReadOnlyList<string> includeAttributes,
        CancellationToken cancellationToken = default);
}
