// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace Honua.Protocols.OData;

internal sealed record ODataGeoParquetStreamOptions
{
    public GeometryLimits GeometryLimits { get; init; } = new();
    public string[]? OutFields { get; init; }
    public ILogger? Logger { get; init; }
    public GeoParquetLimits? EncodingLimits { get; init; }
}
