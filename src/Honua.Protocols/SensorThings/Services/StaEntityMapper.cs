// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.SensorThings.Domain;
using Honua.Protocols.SensorThings.Models;

namespace Honua.Protocols.SensorThings.Services;

internal static class StaEntityMapper
{
    internal static string FormatInstant(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        var pattern = utc.Ticks % TimeSpan.TicksPerMillisecond == 0
            ? "yyyy-MM-dd'T'HH:mm:ss.fff'Z'"
            : "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
        return utc.ToString(pattern, CultureInfo.InvariantCulture);
    }

    public static StaObservation MapObservation(SensorThingsObservation observation, string staBase)
    {
        // FeaturesOfInterest has no entity read surface in this Preview adapter.
        // An opaque ingest identifier is not a resolvable entity: do not advertise it.
        return new StaObservation
        {
            IotId = observation.Id,
            IotSelfLink = $"{staBase}/Observations({observation.Id})",
            PhenomenonTime = observation.PhenomenonTimeEnd is { } end ? $"{FormatInstant(observation.PhenomenonTime)}/{FormatInstant(end)}" : FormatInstant(observation.PhenomenonTime),
            ResultTime = observation.ResultTime is { } rt ? FormatInstant(rt) : null,
            Result = ResultValue(observation),
            DatastreamNavigationLink = $"{staBase}/Observations({observation.Id})/Datastream"
        };
    }

    internal static System.Text.Json.JsonElement ResultValue(SensorThingsObservation observation)
    {
        if (observation.JsonResult is { } result) return result;
        using var document = System.Text.Json.JsonDocument.Parse(observation.Result?.ToString("R", CultureInfo.InvariantCulture) ?? "null");
        return document.RootElement.Clone();
    }
}
