// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

public sealed class QueryRelatedRecordsParameters
{
    public required long[] ObjectIds { get; init; }

    public required int RelationshipId { get; init; }

    public string? OutFields { get; init; }

    public string? Where { get; init; }

    public bool ReturnGeometry { get; init; } = true;

    public string F { get; init; } = "json";

    public string? OutSr { get; init; }

    public bool ReturnZ { get; init; }

    public bool ReturnM { get; init; }

    public int? GeometryPrecision { get; init; }

    public double? MaxAllowableOffset { get; init; }

    public string? GdbVersion { get; init; }

    public string? SqlFormat { get; init; }

    public long? HistoricMoment { get; init; }

    public bool ReturnTrueCurves { get; init; }

    public int? ResultOffset { get; init; }

    public int? ResultRecordCount { get; init; }

    public string? OrderByFields { get; init; }

    public bool ReturnCountOnly { get; init; }
}
