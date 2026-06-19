// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

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
        var foiLink = observation.FeatureOfInterestId is { } foiId
            ? $"{staBase}/FeaturesOfInterest({foiId})"
            : $"{staBase}/Observations({observation.Id})/FeatureOfInterest";

        return new StaObservation
        {
            IotId = observation.Id,
            IotSelfLink = $"{staBase}/Observations({observation.Id})",
            PhenomenonTime = Iso(observation.PhenomenonTime),
            ResultTime = observation.ResultTime is { } rt ? Iso(rt) : null,
            Result = observation.Result,
            DatastreamNavigationLink = $"{staBase}/Observations({observation.Id})/Datastream",
            FeatureOfInterestNavigationLink = foiLink
        };
    }

    public static StaDatastream MapDatastream(
        SensorThingsDatastream datastream,
        string staBase,
        IReadOnlyList<StaObservation>? expandedObservations = null)
    {
        string? phenomenonTime = null;
        if (datastream.PhenomenonTimeStart is { } start && datastream.PhenomenonTimeEnd is { } end)
        {
            phenomenonTime = $"{Iso(start)}/{Iso(end)}";
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
            Observations = expandedObservations
        };
    }
}
