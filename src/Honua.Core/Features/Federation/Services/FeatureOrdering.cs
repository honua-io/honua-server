// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.Federation.Services;

/// <summary>
/// Applies the shared feature ordering semantics with a deterministic object-id tie-breaker.
/// </summary>
public static class FeatureOrdering
{
    public static ImmutableArray<Feature> Apply(
        ImmutableArray<Feature> features,
        ImmutableArray<OrderByClause> orderBy)
    {
        var stableInput = features
            .OrderBy(static feature => feature.Id)
            .ToImmutableArray();
        return FederationLocalRefinement.ApplyOrderBy(stableInput, orderBy);
    }
}
