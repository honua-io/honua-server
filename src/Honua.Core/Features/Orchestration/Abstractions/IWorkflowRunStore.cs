// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Orchestration.Domain;

namespace Honua.Core.Features.Orchestration.Abstractions;

public interface IWorkflowRunStore : IOperationStore
{
    Task<bool> TryCreateAsync(
        WorkflowRun run,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default);

    Task<WorkflowRun?> GetAsync(
        string runId,
        CancellationToken cancellationToken = default);

    Task SetAsync(
        WorkflowRun run,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkflowRun>> ListActiveAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkflowRun>> ListByWorkflowAsync(
        string workflowId,
        CancellationToken cancellationToken = default);
}
