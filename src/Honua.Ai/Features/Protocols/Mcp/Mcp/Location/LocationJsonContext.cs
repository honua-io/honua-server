// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Ai.Protocols.Mcp.Location;

[JsonSerializable(typeof(McpGeocodeArgument))]
[JsonSerializable(typeof(McpGeocodeOutput))]
[JsonSerializable(typeof(McpGeocodeCandidate))]
[JsonSerializable(typeof(McpGeocodeAddressesArgument))]
[JsonSerializable(typeof(McpGeocodeAddressesOutput))]
[JsonSerializable(typeof(McpGeocodeAddressResult))]
[JsonSerializable(typeof(McpGeocodeAddressLocation))]
[JsonSerializable(typeof(McpRouteArgument))]
[JsonSerializable(typeof(McpRouteStopInput))]
[JsonSerializable(typeof(McpRouteOutput))]
[JsonSerializable(typeof(McpRouteDirectionStep))]
[JsonSerializable(typeof(JsonElement))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class LocationJsonContext : JsonSerializerContext;
