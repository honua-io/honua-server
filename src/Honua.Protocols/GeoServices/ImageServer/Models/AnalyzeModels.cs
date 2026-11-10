// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.ImageServer.Models;

public sealed class RasterFunctionDocument
{
    [JsonPropertyName("rasterFunction")]
    public required string RasterFunction { get; init; }

    [JsonPropertyName("rasterFunctionArguments")]
    public Dictionary<string, object?>? RasterFunctionArguments { get; init; }

    [JsonPropertyName("outputPixelType")]
    public string? OutputPixelType { get; init; }
}

public sealed class AnalyzeResponse
{
    [JsonPropertyName("rasterFunction")]
    public required string RasterFunction { get; init; }

    [JsonPropertyName("chainDepth")]
    public int ChainDepth { get; init; }

    [JsonPropertyName("executedFunctions")]
    public string[] ExecutedFunctions { get; init; } = [];

    [JsonPropertyName("outputPixelType")]
    public required string OutputPixelType { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "success";
}
