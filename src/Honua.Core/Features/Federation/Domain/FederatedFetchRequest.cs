// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Linq;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.Federation.Domain;

/// <summary>
/// The request a <see cref="Abstractions.IFederatedSourceConnector"/> receives when the
/// federation executor fetches candidate rows from a remote source. It pairs the canonical
/// <see cref="FeatureQuery"/> with the <see cref="FederationQueryPlan"/> so a connector knows
/// which predicates the planner decided to push down (and therefore must apply remotely) and
/// which the federation layer will refine locally afterwards.
/// </summary>
/// <param name="Source">The descriptor for the target remote source.</param>
/// <param name="Query">The canonical query whose pushed-down predicates the connector applies.</param>
/// <param name="Plan">The push-down / local-refinement plan for this source.</param>
public readonly record struct FederatedFetchRequest(
    FederatedSourceDescriptor Source,
    FeatureQuery Query,
    FederationQueryPlan Plan)
{
    /// <summary>
    /// Indicates whether a predicate family should be applied by the remote source. A
    /// predicate that does not appear in the plan is absent from the query and therefore
    /// nothing to apply.
    /// </summary>
    /// <param name="predicate">The predicate family to test.</param>
    /// <returns><see langword="true"/> when the connector should apply the predicate remotely.</returns>
    public bool ShouldPushDown(FederationPredicateKind predicate) =>
        // A decision that is absent from the plan defaults to Predicate=0/PushedDown=false,
        // which is exactly the "not pushed down" result callers expect for a missing predicate.
        Plan.Decisions.FirstOrDefault(decision => decision.Predicate == predicate).PushedDown;
}
