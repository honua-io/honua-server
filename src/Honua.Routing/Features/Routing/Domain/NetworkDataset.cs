// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Routing.Features.Routing.Domain;

/// <summary>
/// An addressable network dataset (Phase 0 of #1882): a named routing topology the
/// provider resolves edge/vertex table names from, rather than the previously
/// hardcoded <c>public.ways</c> / <c>public.ways_vertices_pgr</c>. Making the
/// network addressable is the prerequisite for multi-dataset routing and (later
/// phases) network-dataset editing; Phase 0 ships the registry and dynamic table
/// resolution only — there is no editing surface yet.
/// </summary>
/// <param name="Id">
/// Stable identifier of the dataset (e.g. <c>"default"</c>). Selected via
/// <see cref="RoutingConfiguration.NetworkDatasetId"/>.
/// </param>
/// <param name="Name">Human-readable display name.</param>
/// <param name="EdgeTable">
/// Schema-qualified edge table name (pgRouting <c>ways</c>-shaped: <c>gid</c>,
/// <c>source</c>, <c>target</c>, <c>cost</c>, <c>reverse_cost</c>, <c>the_geom</c>).
/// </param>
/// <param name="VertexTable">
/// Schema-qualified vertex table name (pgRouting <c>ways_vertices_pgr</c>-shaped:
/// <c>id</c>, <c>the_geom</c>).
/// </param>
/// <param name="Srid">Spatial reference (SRID) of the topology geometries.</param>
public sealed record NetworkDataset(
    string Id,
    string Name,
    string EdgeTable,
    string VertexTable,
    int Srid = 4326)
{
    /// <summary>
    /// Identifier of the built-in default dataset that maps to the existing
    /// <c>public.ways</c> topology, preserving the pre-#1882 behaviour.
    /// </summary>
    public const string DefaultId = "default";

    /// <summary>
    /// The built-in default dataset: the existing osm2pgrouting-compatible
    /// <c>public.ways</c> / <c>public.ways_vertices_pgr</c> topology in WGS84.
    /// </summary>
    public static NetworkDataset Default { get; } = new(
        DefaultId,
        "Default network dataset",
        "public.ways",
        "public.ways_vertices_pgr",
        4326);
}
