// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Temporal.Domain;

namespace Honua.Core.Features.Temporal.Abstractions;

// A rollback submits a new forward edit and never deletes the change log.
public interface ITemporalRollbackRunner
{
    Task<TemporalRollbackJobHandle> SubmitRollbackAsync(
        string serviceId,
        int layerId,
        int storageLayerId,
        ResolvedTemporalCheckpoint target,
        string? reason,
        CancellationToken cancellationToken = default);
}
