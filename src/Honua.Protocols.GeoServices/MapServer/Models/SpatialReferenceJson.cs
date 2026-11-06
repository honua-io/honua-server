// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Shared.Models;

namespace Honua.Protocols.GeoServices.MapServer.Models;

internal sealed class SpatialReferenceJson
{
    [JsonPropertyName("wkid")]
    public required int Wkid { get; init; }

    [JsonPropertyName("latestWkid")]
    public int? LatestWkid { get; init; }

    [JsonPropertyName("vcsWkid")]
    public int? VcsWkid { get; init; }

    [JsonPropertyName("latestVcsWkid")]
    public int? LatestVcsWkid { get; init; }

    [JsonPropertyName("wkt")]
    public string? Wkt { get; init; }

    internal static SpatialReferenceJson FromSpatialReference(SpatialReference sr) => new()
    {
        Wkid = sr.Wkid,
        LatestWkid = sr.LatestWkid,
        VcsWkid = sr.VcsWkid,
        LatestVcsWkid = sr.LatestVcsWkid,
        Wkt = sr.Wkt
    };
}
