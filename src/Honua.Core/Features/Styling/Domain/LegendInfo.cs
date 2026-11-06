// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Styling.Domain;

public sealed class LegendInfo
{
    public required string Title { get; init; }

    public required IReadOnlyList<LegendEntry> Entries { get; init; }
}

public sealed class LegendEntry
{
    public required string Label { get; init; }

    public required string Color { get; init; }

    public double? MinValue { get; init; }

    public double? MaxValue { get; init; }
}
