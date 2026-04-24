// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.ImageServer.Models;

/// <summary>
/// Raster function chain document accepted by the internal raster-function analysis helper.
/// </summary>
public sealed class RasterFunctionDocument
{
    /// <summary>
    /// Name of the function (e.g. <c>Identity</c>, <c>Stretch</c>, <c>Clip</c>).
    /// </summary>
    [JsonPropertyName("rasterFunction")]
    public required string RasterFunction { get; init; }

    /// <summary>
    /// Free-form arguments for the function. Nested function chains live here under
    /// the <c>Raster</c> argument.
    /// </summary>
    [JsonPropertyName("rasterFunctionArguments")]
    public Dictionary<string, object?>? RasterFunctionArguments { get; init; }

    /// <summary>
    /// Optional pixel type override for the chain output (e.g. <c>U8</c>).
    /// </summary>
    [JsonPropertyName("outputPixelType")]
    public string? OutputPixelType { get; init; }
}

/// <summary>
/// JSON descriptor returned by the internal raster-function analysis helper when <c>f=json</c> is requested.
/// </summary>
public sealed class AnalyzeResponse
{
    /// <summary>
    /// The validated and normalised function chain echoed back to the caller.
    /// </summary>
    [JsonPropertyName("rasterFunction")]
    public required string RasterFunction { get; init; }

    /// <summary>
    /// Depth of the chain that was executed (root = 1).
    /// </summary>
    [JsonPropertyName("chainDepth")]
    public int ChainDepth { get; init; }

    /// <summary>
    /// Names of the functions executed, in depth-first order.
    /// </summary>
    [JsonPropertyName("executedFunctions")]
    public string[] ExecutedFunctions { get; init; } = [];

    /// <summary>
    /// Output pixel type produced by the chain.
    /// </summary>
    [JsonPropertyName("outputPixelType")]
    public required string OutputPixelType { get; init; }

    /// <summary>
    /// Stable status indicator for the analysis. <c>success</c> on a 200 response.
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = "success";
}
