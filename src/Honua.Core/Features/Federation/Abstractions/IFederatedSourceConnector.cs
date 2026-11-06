// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Federation.Domain;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.Federation.Abstractions;

// A connector is the only federation type that performs remote I/O; the executor applies the per-source timeout and circuit breaker.
public interface IFederatedSourceConnector
{
    FederatedSourceKind Kind { get; }

    Task<ImmutableArray<Feature>> FetchAsync(FederatedFetchRequest request, CancellationToken cancellationToken);
}
