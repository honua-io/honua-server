// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Routing.Features.Routing.Domain;

namespace Honua.Routing.Features.Routing.Providers;

internal static class RoutingCostUnitConverter
{
    private const double SecondsPerMinute = 60.0;
    private const double MinutesPerHour = 60.0;

    public static double CostToMinutes(double cost, RoutingCostUnit unit) => unit switch
    {
        RoutingCostUnit.Seconds => cost / SecondsPerMinute,
        RoutingCostUnit.Hours => cost * MinutesPerHour,
        _ => cost,
    };

    public static double MinutesToCost(double minutes, RoutingCostUnit unit) => unit switch
    {
        RoutingCostUnit.Seconds => minutes * SecondsPerMinute,
        RoutingCostUnit.Hours => minutes / MinutesPerHour,
        _ => minutes,
    };
}
