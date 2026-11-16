// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

public interface IReplicaEditApplier
{
    Task<ReplicaLayerApplyResult> ApplyAsync(
        string serviceId,
        int publicLayerId,
        ImmutableArray<ReplicaUploadEdit> edits,
        bool rollbackOnFailure,
        ImmutableArray<FeatureEditPrecondition> preconditions = default,
        CancellationToken cancellationToken = default);
}
