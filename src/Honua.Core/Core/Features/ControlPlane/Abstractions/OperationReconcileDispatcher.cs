// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Abstractions;

public enum OperationKind
{
    DeployWorkflow,

    ExecutionJob,

    MetadataRelease,

    CoordinatedRelease,

    PreMigrationRestore
}

public readonly record struct OperationRef(OperationKind Kind, string OperationId);

public interface IOperationReconcileDispatcher
{
    Task ReconcileOnceAsync(OperationRef op, CancellationToken cancellationToken = default);
}
