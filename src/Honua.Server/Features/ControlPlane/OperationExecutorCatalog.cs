// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Guardrails.Domain;

namespace Honua.ControlPlane;

// The supported kinds come from the same registrations the gateway resolves, so the catalog and the executors cannot drift.
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
