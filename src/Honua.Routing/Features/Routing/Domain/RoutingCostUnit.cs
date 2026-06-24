// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Routing.Features.Routing.Domain;

/// <summary>
/// Declares what the topology's <c>cost</c>/<c>reverse_cost</c> edge weights
/// physically represent, so the routing provider can convert the raw pgRouting
/// graph cost into the travel-time minutes the protocol surface (Esri NAServer
/// <c>defaultBreaks</c>/<c>defaultCutoff</c> and <c>TotalTravelTime</c>) expects.
/// </summary>
/// <remarks>
/// The osm2pgrouting-compatible <c>ways.cost</c> column is a <em>generic graph
/// weight</em>: depending on how the topology was generated it may carry seconds
/// (the osm2pgrouting <c>cost_s</c> convention), edge length in degrees, or a
/// pre-baked travel-time value. The honua topology provisioned by migration
/// 043_CreatePgRoutingTopology.sql does not declare a unit (it documents the
/// weight as "mapped to both length-meters and travel-time by the routing provider
/// for the MVP"), so the contract must be declared by the operator rather than
/// inferred. This enum names the realistic options and lets a deployment whose
/// topology uses a known unit opt into an explicit, correct conversion instead of
/// the implicit 1:1 assumption.
/// </remarks>
public enum RoutingCostUnit
{
    /// <summary>
    /// The cost weight already is travel-time in minutes (1 cost unit = 1 minute).
    /// This is the historical MVP assumption and remains the default so existing
    /// deployments are unchanged; the conversion is the identity.
    /// </summary>
    Minutes = 0,

    /// <summary>
    /// The cost weight is travel-time in seconds (the osm2pgrouting <c>cost_s</c>
    /// convention). Time outputs are divided by 60 and request breaks/cutoffs
    /// (which arrive in minutes) are multiplied by 60 before being passed to
    /// pgRouting as a raw cost cutoff.
    /// </summary>
    Seconds = 1,

    /// <summary>
    /// The cost weight is travel-time in hours. Time outputs are multiplied by 60
    /// and request breaks/cutoffs (minutes) are divided by 60 before being passed
    /// to pgRouting as a raw cost cutoff.
    /// </summary>
    Hours = 2,
}
