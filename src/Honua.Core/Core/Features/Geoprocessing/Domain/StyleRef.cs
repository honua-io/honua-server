// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Domain;

public sealed record StyleRef
{
    public required string StyleId { get; init; }

    public string? Label { get; init; }

    public string? PresetId { get; init; }
}
