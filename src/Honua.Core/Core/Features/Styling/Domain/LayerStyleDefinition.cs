// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Styling.Domain;

public sealed record LayerStyleDefinition
{
    public required int LayerId { get; init; }

    public string? MapLibreStyleJson { get; init; }

    public string? DrawingInfoJson { get; init; }

    public int StyleVersion { get; init; }

    public DateTimeOffset? StyleRevisedAt { get; init; }

    public string? StyleRevisedBy { get; init; }

    public string? StyleChangeSummary { get; init; }
}
