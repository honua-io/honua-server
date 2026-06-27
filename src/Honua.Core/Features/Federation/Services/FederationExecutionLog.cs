// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Core.Features.Federation.Services;

/// <summary>
/// Source-generated structured logging for the federated-query executor (issue #341).
/// </summary>
internal static partial class FederationExecutionLog
{
    [LoggerMessage(EventId = 4510, Level = LogLevel.Debug,
        Message = "Executed federated query against source '{SourceId}' in {ElapsedMs}ms ({Count} rows)")]
    public static partial void SourceExecuted(ILogger logger, string sourceId, double elapsedMs, int count);

    [LoggerMessage(EventId = 4511, Level = LogLevel.Warning,
        Message = "Federated source '{SourceId}' unavailable ({Reason})")]
    public static partial void SourceUnavailable(ILogger logger, string sourceId, string reason, Exception exception);

    [LoggerMessage(EventId = 4512, Level = LogLevel.Warning,
        Message = "Federated source '{SourceId}' circuit opened for {BreakMs}ms")]
    public static partial void CircuitOpened(ILogger logger, string sourceId, double breakMs);
}
