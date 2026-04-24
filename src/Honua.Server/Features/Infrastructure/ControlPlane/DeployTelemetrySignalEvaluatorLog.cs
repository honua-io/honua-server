// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.ControlPlane;

internal static partial class DeployTelemetrySignalEvaluatorLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to evaluate deploy telemetry signals for operation {OperationId}")]
    public static partial void EvaluationFailed(ILogger logger, string operationId, Exception exception);
}
