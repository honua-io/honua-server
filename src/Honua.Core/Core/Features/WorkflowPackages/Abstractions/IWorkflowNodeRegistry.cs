// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.WorkflowPackages.Domain;

namespace Honua.Core.Features.WorkflowPackages.Abstractions;

public interface IWorkflowNodeRegistry
{
    Task<WorkflowNodeRegistrySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    Task<WorkflowNodeDefinition?> GetNodeAsync(string nodeTypeId, CancellationToken cancellationToken = default);
}

public interface IWorkflowNodeProvider
{
    string ProviderId { get; }

    string Version { get; }

    Task<IReadOnlyList<WorkflowNodeDefinition>> ListNodesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetWarningsAsync(CancellationToken cancellationToken = default);
}
