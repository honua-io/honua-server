// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Operations.Abstractions;

/// <summary>
/// Marks an <see cref="IOperationExecutor"/> whose <see cref="IOperationExecutor.SubmitAsync"/>
/// honors <see cref="Honua.Core.Features.Operations.Domain.OperationRequest.DryRun"/> by calling the declared dry-run route.
/// The dispatcher invokes that executor on a dry run. Executors that do not implement this
/// marker keep the synthetic completed result, so a dry-run flag cannot actuate them.
/// </summary>
public interface IOperationDryRunExecutor : IOperationExecutor
{
    /// <summary>
    /// Confirms the executor performs its dry-run path from <see cref="IOperationExecutor.SubmitAsync"/>.
    /// </summary>
    bool ExecutesDryRun => true;
}
