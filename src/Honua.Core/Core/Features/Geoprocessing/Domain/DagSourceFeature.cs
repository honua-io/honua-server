// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Generic;

namespace Honua.Core.Features.Geoprocessing.Domain;

public sealed record DagSourceFeature
{
    public string? GeometryGeoJson { get; init; }

    public required IReadOnlyDictionary<string, object?> Attributes { get; init; }
}
