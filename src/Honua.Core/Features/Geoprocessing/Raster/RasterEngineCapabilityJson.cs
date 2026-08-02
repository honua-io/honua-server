// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Geoprocessing.Raster;

/// <summary>Source-generated JSON metadata for raster engine capability and cost contracts.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(RasterProcessCapability))]
[JsonSerializable(typeof(RasterEngineCapability))]
[JsonSerializable(typeof(RasterFormatRestrictions))]
[JsonSerializable(typeof(RasterCostEstimatorInput))]
[JsonSerializable(typeof(RasterCostEstimate))]
[JsonSerializable(typeof(List<RasterProcessCapability>))]
[JsonSerializable(typeof(List<RasterEngineCapability>))]
[JsonSerializable(typeof(List<RasterInputResidency>))]
[JsonSerializable(typeof(List<RasterOutputSink>))]
[JsonSerializable(typeof(List<string>))]
public sealed partial class RasterEngineCapabilityJsonContext : JsonSerializerContext
{
}

/// <summary>AOT-safe serialization helpers for raster engine capability metadata.</summary>
public static class RasterEngineCapabilityJson
{
    /// <summary>Serializes one process capability through source-generated metadata.</summary>
    public static string Serialize(RasterProcessCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        return JsonSerializer.Serialize(
            capability,
            RasterEngineCapabilityJsonContext.Default.RasterProcessCapability);
    }

    /// <summary>Deserializes one process capability through source-generated metadata.</summary>
    public static RasterProcessCapability Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize(
            json,
            RasterEngineCapabilityJsonContext.Default.RasterProcessCapability)
            ?? throw new JsonException("Raster engine capability cannot be null.");
    }
}
