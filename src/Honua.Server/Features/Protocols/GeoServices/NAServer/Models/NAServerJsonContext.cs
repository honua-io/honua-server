// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Protocols.GeoServices.NAServer.Models;

/// <summary>
/// AOT-compatible JSON serialization context for minimal NAServer wire-format models.
/// </summary>
[JsonSerializable(typeof(NAServerRouteSolveResponse))]
[JsonSerializable(typeof(NAServerClosestFacilityResponse))]
[JsonSerializable(typeof(NAServerServiceAreaResponse))]
[JsonSerializable(typeof(NAServerRouteFeatureSet))]
[JsonSerializable(typeof(NAServerRouteFeature))]
[JsonSerializable(typeof(NAServerRouteFeature[]))]
[JsonSerializable(typeof(NAServerRouteAttributes))]
[JsonSerializable(typeof(NAServerDirection))]
[JsonSerializable(typeof(NAServerDirection[]))]
[JsonSerializable(typeof(NAServerDirectionFeature))]
[JsonSerializable(typeof(NAServerDirectionFeature[]))]
[JsonSerializable(typeof(NAServerDirectionAttributes))]
[JsonSerializable(typeof(NAServerDirectionSummary))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class NAServerJsonContext : JsonSerializerContext;
