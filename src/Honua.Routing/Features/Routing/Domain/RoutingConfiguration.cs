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
}
