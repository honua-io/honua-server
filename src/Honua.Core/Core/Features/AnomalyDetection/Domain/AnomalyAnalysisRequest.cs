// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.AnomalyDetection.Domain;

public sealed record AnomalyAnalysisRequest(
    string TableName,
    string LayerName,
    string? GeometryColumn,
    int DeclaredSrid,
    IReadOnlyList<AnomalyFieldDescriptor> AttributeColumns,
    string ObjectIdColumn = "objectid",
    int MaxSampleFeatures = 5,
    int ScanLimit = 10000);

public sealed record AnomalyFieldDescriptor(
    string Name,
    AnomalyFieldDataType DataType);

public enum AnomalyFieldDataType
{
    Text,

    Numeric,

    Temporal,

    Boolean,

    Other
}
