// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Features.Import.Domain;

public sealed record ImportCoordinateSystem
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? DetectedSrid { get; init; }
    public bool SourceCrsDeclared { get; init; }
    public required string DetectionMethod { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public CoordinateSystemEntry? DetectedCrs { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? EffectiveSourceSrid { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? TargetSrid { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? StoredSrid { get; init; }
    public bool Reprojected { get; init; }
    public bool RequiresSourceOverride { get; init; }
    public static ImportCoordinateSystem FromDetection(int? srid, bool declared, bool formatDefined = false)
        => new()
        {
            DetectedSrid = srid,
            SourceCrsDeclared = declared,
            DetectionMethod = formatDefined ? "format-defined" : declared ? "file-metadata" : "unknown",
            EffectiveSourceSrid = srid,
            StoredSrid = srid,
            RequiresSourceOverride = !srid.HasValue
        };
}
