// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Guardrails.Domain;

namespace Honua.ControlPlane;

/// <summary>
/// Default <see cref="IOperationExecutorCatalog"/> that derives the supported-kinds set directly
/// from the same <see cref="IOperationExecutor"/> registrations the gateway itself resolves
/// (#2563). Registered alongside <see cref="OperationGateway"/> and its executors so the two can
/// never drift apart.
/// </summary>
internal sealed class OperationExecutorCatalog : IOperationExecutorCatalog
{
    public OperationExecutorCatalog(IEnumerable<IOperationExecutor> executors)
    {
        ArgumentNullException.ThrowIfNull(executors);
        SupportedKinds = executors
            .Select(executor => executor.OperationClass)
            .Distinct()
            .ToArray();
    }

    public IReadOnlyCollection<OperationClass> SupportedKinds { get; }
}
