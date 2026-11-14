// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

internal sealed partial class DeployWorkflowReconciler
{
    internal static async Task<WorkflowOperationRecord> AdvanceOrPromoteAsync(
        WorkflowOperationRecord current,
        IDeployBackend backend,
        CancellationToken cancellationToken)
    {
        try
        {
            return await AdvanceOrPromoteCoreAsync(current, backend, cancellationToken).ConfigureAwait(false);
        }
        catch (DeployAdmissionBlockedException ex)
        {
            // Keep this pass's observation and telemetry state; the next pass retries the step.
            return current with { ErrorMessage = ex.Message, UpdatedAt = DateTimeOffset.UtcNow };
        }
    }

    internal static partial class Log
    {
        [LoggerMessage(9027, LogLevel.Warning, "Deploy admission refused for operation {OperationId}: {Reason}")]
        public static partial void DeployAdmissionRefused(ILogger logger, string operationId, string reason);
    }
}
