// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Federation.Domain;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.Federation.Abstractions;

public interface IFederatedQueryExecutor
{
    Task<FederatedQueryResult> ExecuteAsync(
        FederatedSourceDescriptor source,
        FeatureQuery query,
        CancellationToken cancellationToken = default);

    // An unavailable source is recorded as a failure and does not fail the whole request.
    Task<FederatedCombinedResult> ExecuteAsync(
        ImmutableArray<FederatedSourceDescriptor> sources,
        FeatureQuery query,
        CancellationToken cancellationToken = default);
}
