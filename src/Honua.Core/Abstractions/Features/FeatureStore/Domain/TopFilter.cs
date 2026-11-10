// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.FeatureStore.Domain;

public readonly record struct TopFilter
{
    public required ImmutableArray<string> GroupByFields { get; init; }

    public required int TopCount { get; init; }

    public required ImmutableArray<OrderByClause> OrderByFields { get; init; }
}
