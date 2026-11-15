// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Features.ControlPlane.Abstractions;

// Receipt-only recovery must never start new sink work. Null confirms no committed outcome.
public interface IJobCancellationRecoveryExecutor
{
    Task<JobExecutionResult?> RecoverCancellationAsync(
        ExecutionJobRecord job,
        IJobExecutionContext context,
        CancellationToken cancellationToken);
}
