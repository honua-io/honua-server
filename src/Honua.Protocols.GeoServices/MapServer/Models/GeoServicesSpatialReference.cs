// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Shared.Models;

namespace Honua.Protocols.GeoServices.MapServer.Models;

/// <summary>
/// Esri spatial reference information for MapServer responses.
/// </summary>
internal sealed class GeoServicesSpatialReference
{
    /// <summary>
    /// Well-Known ID (EPSG code).
    /// </summary>
    [JsonPropertyName("wkid")]
    public required int Wkid { get; init; }

    /// <summary>
    /// Latest Well-Known ID (for newer EPSG codes).
    /// </summary>
    [JsonPropertyName("latestWkid")]
    public int? LatestWkid { get; init; }

    /// <summary>
    /// Vertical coordinate system WKID.
    /// </summary>
    [JsonPropertyName("vcsWkid")]
    public int? VcsWkid { get; init; }

    /// <summary>
    /// Latest vertical coordinate system WKID.
    /// </summary>
    [JsonPropertyName("latestVcsWkid")]
    public int? LatestVcsWkid { get; init; }

    /// <summary>
    /// Well-Known Text representation.
    /// </summary>
    [JsonPropertyName("wkt")]
    public string? Wkt { get; init; }

    /// <summary>
    /// Creates an GeoServicesSpatialReference from a Core SpatialReference.
    /// </summary>
    internal static GeoServicesSpatialReference FromSpatialReference(SpatialReference sr) => new()
    {
        Wkid = sr.Wkid,
        LatestWkid = sr.LatestWkid,
        VcsWkid = sr.VcsWkid,
        LatestVcsWkid = sr.LatestVcsWkid,
        Wkt = sr.Wkt
    };
}
