// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Routing.Features.Routing.Domain;

namespace Honua.Routing.Features.Routing.Providers;

/// <summary>
/// Single, declared contract for converting between the topology's raw pgRouting
/// graph <c>cost</c>/<c>reverse_cost</c> weight and the travel-time minutes the
/// routing request/response surface speaks in.
/// </summary>
/// <remarks>
/// <para>
/// The Esri NAServer adapter and the protocol-neutral routing models exchange time
/// exclusively in <em>minutes</em> (service-area <c>defaultBreaks</c>, the
/// closest-facility / OD-cost-matrix <c>defaultCutoff</c>, and the
/// <c>TotalTravelTime</c>/<c>TotalTime</c>/<c>ImpedanceMinutes</c> outputs). The raw
/// pgRouting <c>cost</c> column, however, is a generic graph weight whose physical
/// unit depends on how the topology was generated (see
/// <see cref="RoutingCostUnit"/>). This converter is the one place the cost-unit
/// contract is applied, so route time, isochrone <c>@break_cost</c>, and the cost
/// cutoffs in closest-facility / OD-cost-matrix all stay consistent: time outputs go
/// through <see cref="CostToMinutes"/> and request minutes (breaks/cutoffs) go
/// through <see cref="MinutesToCost"/> before being compared against / passed as a
/// raw pgRouting cost.
/// </para>
/// <para>
/// Both directions are exact inverses, so a value that round-trips
/// (minutes → cost → minutes) is unchanged. For the default
/// <see cref="RoutingCostUnit.Minutes"/> the conversion is the identity, preserving
/// the historical MVP behaviour while making the unit explicit rather than implicit.
/// </para>
/// </remarks>
internal static class RoutingCostUnitConverter
{
    private const double SecondsPerMinute = 60.0;
    private const double MinutesPerHour = 60.0;

    /// <summary>
    /// Converts a raw pgRouting graph cost (in the configured
    /// <paramref name="unit"/>) into travel-time minutes for the response surface.
    /// </summary>
    /// <param name="cost">The raw aggregate cost reported by pgRouting.</param>
    /// <param name="unit">The declared physical unit of the cost weight.</param>
    /// <returns>The equivalent travel time in minutes.</returns>
    public static double CostToMinutes(double cost, RoutingCostUnit unit) => unit switch
    {
        RoutingCostUnit.Seconds => cost / SecondsPerMinute,
        RoutingCostUnit.Hours => cost * MinutesPerHour,
        _ => cost,
    };

    /// <summary>
    /// Converts a travel-time value in minutes (a request break or cutoff) into the
    /// raw pgRouting cost unit so it can be compared against / passed as a
    /// <c>@break_cost</c> or cost cutoff.
    /// </summary>
    /// <param name="minutes">The travel-time value in minutes.</param>
    /// <param name="unit">The declared physical unit of the cost weight.</param>
    /// <returns>The equivalent value expressed in the cost unit.</returns>
    public static double MinutesToCost(double minutes, RoutingCostUnit unit) => unit switch
    {
        RoutingCostUnit.Seconds => minutes * SecondsPerMinute,
        RoutingCostUnit.Hours => minutes / MinutesPerHour,
        _ => minutes,
    };
}
