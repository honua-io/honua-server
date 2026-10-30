// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.SensorThings.Domain;
using Honua.Protocols.SensorThings.Models;

namespace Honua.Protocols.SensorThings.Services;

/// <summary>
/// Maps canonical SensorThings domain records to STA-conformance-shaped wire DTOs,
/// emitting the <c>@iot.id</c>, <c>@iot.selfLink</c>, and <c>@iot.navigationLink</c>
/// envelope rooted at the request base path (e.g. <c>https://host/sta/v1.1</c>).
/// </summary>
internal static class StaEntityMapper
{
    /// <summary>
    /// Formats an instant as UTC ISO-8601 with a <c>Z</c> suffix.
    /// Millisecond-aligned values keep three fractional digits. Finer values use seven digits.
    /// The three-digit pattern rounds, so it is only used when the remainder is zero.
    /// </summary>
    internal static string FormatInstant(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        var pattern = utc.Ticks % TimeSpan.TicksPerMillisecond == 0
            ? "yyyy-MM-dd'T'HH:mm:ss.fff'Z'"
            : "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
        return utc.ToString(pattern, CultureInfo.InvariantCulture);
    }

    public static StaThing MapThing(SensorThingsThing thing, string staBase) => new()
    {
        IotId = thing.Id,
        IotSelfLink = $"{staBase}/Things({thing.Id})",
        Name = thing.Name,
        Description = thing.Description,
        DatastreamsNavigationLink = $"{staBase}/Things({thing.Id})/Datastreams"
    };

    public static StaSensor MapSensor(SensorThingsSensor sensor, string staBase) => new()
    {
        IotId = sensor.Id,
        IotSelfLink = $"{staBase}/Sensors({sensor.Id})",
        Name = sensor.Name,
        Description = sensor.Description,
        EncodingType = sensor.EncodingType,
        Metadata = sensor.Metadata,
        DatastreamsNavigationLink = $"{staBase}/Sensors({sensor.Id})/Datastreams"
    };

    public static StaObservedProperty MapObservedProperty(SensorThingsObservedProperty property, string staBase) => new()
    {
        IotId = property.Id,
        IotSelfLink = $"{staBase}/ObservedProperties({property.Id})",
        Name = property.Name,
        Definition = property.Definition,
        Description = property.Description,
        DatastreamsNavigationLink = $"{staBase}/ObservedProperties({property.Id})/Datastreams"
    };

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

    public static StaDatastream MapDatastream(
        SensorThingsDatastream datastream,
        string staBase,
        IReadOnlyList<StaObservation>? expandedObservations = null,
        StaThing? expandedThing = null,
        StaSensor? expandedSensor = null,
        StaObservedProperty? expandedObservedProperty = null)
    {
        string? phenomenonTime = null;
        if (datastream.PhenomenonTimeStart is { } start && datastream.PhenomenonTimeEnd is { } end)
        {
            phenomenonTime = $"{FormatInstant(start)}/{FormatInstant(end)}";
        }

        return new StaDatastream
        {
            IotId = datastream.Id,
            IotSelfLink = $"{staBase}/Datastreams({datastream.Id})",
            Name = datastream.Name,
            Description = datastream.Description,
            ObservationType = datastream.ObservationType,
            UnitOfMeasurement = new StaUnitOfMeasurement
            {
                Name = datastream.UnitName,
                Symbol = datastream.UnitSymbol,
                Definition = datastream.UnitDefinition
            },
            PhenomenonTime = phenomenonTime,
            ObservationsNavigationLink = $"{staBase}/Datastreams({datastream.Id})/Observations",
            ThingNavigationLink = $"{staBase}/Datastreams({datastream.Id})/Thing",
            SensorNavigationLink = $"{staBase}/Datastreams({datastream.Id})/Sensor",
            ObservedPropertyNavigationLink = $"{staBase}/Datastreams({datastream.Id})/ObservedProperty",
            Observations = expandedObservations,
            Thing = expandedThing,
            Sensor = expandedSensor,
            ObservedProperty = expandedObservedProperty
        };
    }
}
