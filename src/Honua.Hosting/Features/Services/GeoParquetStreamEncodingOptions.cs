// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace Honua.Infrastructure.Services;

public sealed record GeoParquetStreamEncodingOptions
{
    public string ObjectIdFieldName { get; init; } = string.Empty;
    public bool ReturnGeometry { get; init; }
    public int? OutputSrid { get; init; }
    public bool ReturnZ { get; init; }
    public bool ReturnM { get; init; }
    public GeometryLimits GeometryLimits { get; init; } = new();
    public string[]? OutFields { get; init; }
    public ILogger? Logger { get; init; }
    public GeoParquetLimits? EncodingLimits { get; init; }
    public IReadOnlyDictionary<string, string>? FileMetadata { get; init; }
}
