// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Routing.Features.Routing.Domain;

/// <summary>
/// Bound options for the routing subsystem. Mirrors the geocoding configuration
/// pattern (a strongly-typed options object plus a validator) without the
/// multi-provider failover surface routing does not need for the MVP. Bound from
/// the <c>"Routing"</c> configuration section.
/// </summary>
public sealed class RoutingConfiguration
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "Routing";

    /// <summary>
    /// Routing provider selector (e.g. <c>"pgrouting"</c>, <c>"mock"</c>).
    /// Defaults to the pgRouting provider.
    /// </summary>
    public string Provider { get; set; } = "pgrouting";

    /// <summary>
    /// Maximum number of stops accepted on a single route solve. Caps serial
    /// per-leg database round-trips and bounds fan-out (DoS guard).
    /// </summary>
    public int MaxStops { get; set; } = 1000;

    /// <summary>
    /// Maximum number of facilities accepted on a single service-area solve. Each
    /// facility drives one driving-distance query per break, so this bounds total
    /// fan-out (DoS guard).
    /// </summary>
    public int MaxFacilities { get; set; } = 1000;

    /// <summary>
    /// Maximum number of distinct positive break cutoffs accepted on a single
    /// service-area solve. Each break drives one driving-distance query per
    /// facility (DoS guard).
    /// </summary>
    public int MaxBreaks { get; set; } = 50;

    /// <summary>
    /// Maximum number of barriers (across point/line/polygon families) accepted on
    /// a single solve. Each barrier widens the edge-exclusion predicate, so this
    /// bounds the cost of the spatial filter (DoS guard).
    /// </summary>
    public int MaxBarriers { get; set; } = 1000;

    /// <summary>
    /// Maximum number of incidents accepted on a single closest-facility solve.
    /// Each incident drives a one-to-many cost query plus per-result route
    /// materialization, so this bounds total fan-out (DoS guard).
    /// </summary>
    public int MaxIncidents { get; set; } = 1000;

    /// <summary>
    /// Maximum number of facilities accepted on a single closest-facility solve.
    /// Bounds the candidate set ranked per incident (DoS guard).
    /// </summary>
    public int MaxClosestFacilities { get; set; } = 1000;

    /// <summary>
    /// Maximum number of origins accepted on a single OD cost matrix solve. The
    /// matrix is O(origins × destinations), so origins must be bounded (DoS guard).
    /// </summary>
    public int MaxOrigins { get; set; } = 1000;

    /// <summary>
    /// Maximum number of destinations accepted on a single OD cost matrix solve.
    /// The matrix is O(origins × destinations), so destinations must be bounded
    /// (DoS guard).
    /// </summary>
    public int MaxDestinations { get; set; } = 1000;

    /// <summary>
    /// Maximum number of candidate facilities accepted on a single
    /// location-allocation solve. The candidate × demand cost matrix is
    /// O(facilities × demand), so candidates must be bounded (DoS guard).
    /// </summary>
    public int MaxLocationAllocationFacilities { get; set; } = 1000;

    /// <summary>
    /// Maximum number of demand points accepted on a single location-allocation
    /// solve (DoS guard, mirrors <see cref="MaxLocationAllocationFacilities"/>).
    /// </summary>
    public int MaxDemandPoints { get; set; } = 1000;

    /// <summary>
    /// Identifier of the active network dataset (Phase 0 of #1882). Selects the
    /// registered edge/vertex topology the routing provider resolves table names
    /// from. Defaults to <c>"default"</c>, which maps to the existing
    /// <c>public.ways</c> / <c>public.ways_vertices_pgr</c> topology, so the default
    /// behaviour is unchanged.
    /// </summary>
    public string NetworkDatasetId { get; set; } = "default";

    /// <summary>
    /// Declares what the active topology's <c>cost</c>/<c>reverse_cost</c> edge
    /// weights physically represent, so the provider can convert the raw pgRouting
    /// graph cost into the travel-time minutes the request/response surface speaks in
    /// (service-area breaks, closest-facility/OD cutoffs, and <c>TotalTravelTime</c>).
    /// </summary>
    /// <remarks>
    /// The osm2pgrouting-compatible <c>ways.cost</c> column is a generic graph weight
    /// with no declared unit in this codebase's topology (migration
    /// 043_CreatePgRoutingTopology.sql documents it as "mapped to both length-meters
    /// and travel-time by the routing provider for the MVP"), so the unit cannot be
    /// inferred from the data and must be declared here. Defaults to
    /// <see cref="RoutingCostUnit.Minutes"/> (1 cost unit = 1 minute), preserving the
    /// historical MVP behaviour; set it to <see cref="RoutingCostUnit.Seconds"/> for a
    /// topology built with the osm2pgrouting <c>cost_s</c> (seconds) convention, so
    /// drive-time outputs and isochrone cutoffs are unit-correct.
    /// </remarks>
    public RoutingCostUnit CostUnit { get; set; } = RoutingCostUnit.Minutes;
}
