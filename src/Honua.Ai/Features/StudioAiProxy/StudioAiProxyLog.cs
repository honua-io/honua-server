// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Ai.StudioAiProxy.Domain;
using Microsoft.Extensions.Logging;

namespace Honua.Ai.StudioAiProxy;

/// <summary>
/// Structured logging for the Studio AI proxy. Event ID range: 6750-6799. Telemetry activities use
/// the <c>honua.studioai.*</c> shape.
/// </summary>
internal static partial class StudioAiProxyLog
{
    [LoggerMessage(
        EventId = 6750,
        Level = LogLevel.Information,
        Message = "Studio AI proxy chat requested (provider={Provider}, kind={Kind}, model={Model})")]
    public static partial void ChatRequested(ILogger logger, string provider, string kind, string model);

    [LoggerMessage(
        EventId = 6751,
        Level = LogLevel.Information,
        Message = "Studio AI proxy chat completed (provider={Provider}, stopReason={StopReason}, latencyMs={LatencyMs})")]
    public static partial void ChatCompleted(ILogger logger, string provider, StudioAiStopReason stopReason, long latencyMs);

    [LoggerMessage(
        EventId = 6752,
        Level = LogLevel.Warning,
        Message = "Studio AI proxy provider returned non-success status: Provider={Provider}, Status={StatusCode}, Body={ErrorBody}")]
    public static partial void ProviderHttpError(ILogger logger, string provider, int statusCode, string errorBody);

    [LoggerMessage(
        EventId = 6753,
        Level = LogLevel.Warning,
        Message = "Studio AI proxy request timed out: Provider={Provider}")]
    public static partial void ProviderTimeout(ILogger logger, string provider);

    [LoggerMessage(
        EventId = 6754,
        Level = LogLevel.Warning,
        Message = "Studio AI proxy request failed (network/transport): Provider={Provider}")]
    public static partial void ProviderRequestFailed(ILogger logger, string provider, Exception exception);

    [LoggerMessage(
        EventId = 6755,
        Level = LogLevel.Warning,
        Message = "Studio AI proxy response could not be parsed: Provider={Provider}")]
    public static partial void ProviderResponseParseFailed(ILogger logger, string provider, Exception exception);

    [LoggerMessage(
        EventId = 6756,
        Level = LogLevel.Warning,
        Message = "Studio AI proxy chat request rejected: {Reason}")]
    public static partial void ChatRequestRejected(ILogger logger, string reason);
}
