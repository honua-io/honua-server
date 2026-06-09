// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.ControlPlane;

internal static partial class DeployTelemetrySignalEvaluatorLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to evaluate deploy telemetry signals for operation {OperationId}")]
    public static partial void EvaluationFailed(ILogger logger, string operationId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deploy telemetry connection '{ConnectionId}' uses unsupported provider '{Provider}' for operation {OperationId}; auto-rollback signals are disabled until a supported provider is configured (supported: {SupportedProviders}).")]
    public static partial void UnsupportedProvider(ILogger logger, string operationId, string connectionId, string provider, string supportedProviders);
}
