// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Linq;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.Federation.Domain;

public readonly record struct FederatedFetchRequest(
    FederatedSourceDescriptor Source,
    FeatureQuery Query,
    FederationQueryPlan Plan)
{
    public bool ShouldPushDown(FederationPredicateKind predicate) =>
        // A decision that is absent from the plan defaults to Predicate=0/PushedDown=false,
        // which is exactly the "not pushed down" result callers expect for a missing predicate.
        Plan.Decisions.FirstOrDefault(decision => decision.Predicate == predicate).PushedDown;
}
