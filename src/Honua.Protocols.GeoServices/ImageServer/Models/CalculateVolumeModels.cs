// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.ImageServer.Models;

/// <summary>
/// Response for the ImageServer <c>calculateVolume</c> operation. Mirrors the documented ArcGIS
/// Enterprise REST response shape: a <c>results</c> array with one entry per input geometry
/// (area-of-interest), each carrying the cut/fill volumes, 2D surface area, and elevation
/// statistics computed against the layer's associated DEM surface. See ADR-0065.
/// </summary>
public sealed class CalculateVolumeResponse
{
    /// <summary>One volume result per input area-of-interest geometry, in request order.</summary>
    [JsonPropertyName("results")]
    public required IReadOnlyList<CalculateVolumeResult> Results { get; init; }
}

/// <summary>
/// The cut/fill volume result for a single area-of-interest, computed by integrating the DEM
/// elevation surface against the base plane over the DEM pixels inside the AOI:
/// <c>cut = Σ_(e &gt; z0)(e − z0) · pixelArea</c> (material above the base, to be excavated) and
/// <c>fill = Σ_(e &lt; z0)(e − z0) · pixelArea</c> (void below the base, to be filled; negative,
/// matching the Esri convention). Volumes and area are expressed in the DEM's linear map units
/// (cubic / square units — cubic / square meters for a projected metric DEM).
/// </summary>
public sealed class CalculateVolumeResult
{
    /// <summary>2D surface area of the DEM pixels inside the AOI (square map units).</summary>
    [JsonPropertyName("area")]
    public required double Area { get; init; }

    /// <summary>Volume of material above the base plane requiring excavation (cubic map units).</summary>
    [JsonPropertyName("cut")]
    public required double Cut { get; init; }

    /// <summary>
    /// Volume below the base plane requiring fill (cubic map units), reported as a negative value
    /// per the Esri convention.
    /// </summary>
    [JsonPropertyName("fill")]
    public required double Fill { get; init; }

    /// <summary>Minimum DEM elevation sampled inside the AOI.</summary>
    [JsonPropertyName("minz")]
    public required double MinZ { get; init; }

    /// <summary>Maximum DEM elevation sampled inside the AOI.</summary>
    [JsonPropertyName("maxz")]
    public required double MaxZ { get; init; }

    /// <summary>Mean DEM elevation sampled inside the AOI.</summary>
    [JsonPropertyName("meanz")]
    public required double MeanZ { get; init; }
}
