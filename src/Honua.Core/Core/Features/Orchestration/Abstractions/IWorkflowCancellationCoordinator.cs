// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Orchestration.Domain;

namespace Honua.Core.Features.Orchestration.Abstractions;

public interface IWorkflowCancellationCoordinator
{
    Task<WorkflowCancellationOutcome> CancelRunAsync(
        string runId,
        CancellationToken cancellationToken = default);
}
