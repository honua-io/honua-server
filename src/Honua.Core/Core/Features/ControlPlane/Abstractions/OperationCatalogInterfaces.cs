// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Features.ControlPlane.Abstractions;

public interface IDeployTargetRegistry
{
    Task<IReadOnlyList<DeployTargetDefinition>> ListAsync(CancellationToken cancellationToken = default);

    Task<DeployTargetDefinition?> GetAsync(string targetId, CancellationToken cancellationToken = default);
}

public interface IExecutionJobDefinitionRegistry
{
    Task<IReadOnlyList<ExecutionJobDefinition>> ListAsync(CancellationToken cancellationToken = default);

    Task<ExecutionJobDefinition?> GetAsync(string workloadId, CancellationToken cancellationToken = default);
}
