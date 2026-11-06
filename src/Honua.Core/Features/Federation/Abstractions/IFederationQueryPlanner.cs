// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Federation.Domain;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.Federation.Abstractions;

public interface IFederationQueryPlanner
{
    FederatedSourceCapabilities ResolveCapabilities(FederatedSourceKind kind);

    FederationQueryPlan Plan(FederatedSourceDescriptor source, in FeatureQuery query, bool joinsLocalLayer);
}
