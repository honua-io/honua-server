// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Protocols.SensorThings.Models;

namespace Honua.Protocols.SensorThings;

/// <summary>
/// Source-generated JSON serialization context for OGC SensorThings API wire DTOs.
/// Property names are controlled per-member with <c>[JsonPropertyName]</c> (STA uses
/// <c>@iot.*</c> annotation members), so no naming policy is applied here.
/// </summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StaEntitySet<StaThing>))]
[JsonSerializable(typeof(StaEntitySet<StaSensor>))]
[JsonSerializable(typeof(StaEntitySet<StaObservedProperty>))]
[JsonSerializable(typeof(StaEntitySet<StaDatastream>))]
[JsonSerializable(typeof(StaEntitySet<StaObservation>))]
[JsonSerializable(typeof(StaThing))]
[JsonSerializable(typeof(StaSensor))]
[JsonSerializable(typeof(StaObservedProperty))]
[JsonSerializable(typeof(StaDatastream))]
[JsonSerializable(typeof(StaObservation))]
[JsonSerializable(typeof(StaUnitOfMeasurement))]
internal sealed partial class SensorThingsJsonContext : JsonSerializerContext
{
}
