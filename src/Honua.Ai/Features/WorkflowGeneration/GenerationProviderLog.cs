// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Ai.WorkflowGeneration;

/// <summary>
/// Source-generated structured log methods for the AI generation provider call path,
/// shared across the map, app, form, dashboard, report, analysis, and query generation services.
/// Event ID range: 6700-6749.
/// </summary>
internal static partial class GenerationProviderLog
{
    [LoggerMessage(
        EventId = 6700,
        Level = LogLevel.Warning,
        Message = "Generation provider returned non-success status: Provider={Provider}, Status={StatusCode}, Body={ErrorBody}")]
    public static partial void ProviderHttpError(ILogger logger, string provider, int statusCode, string errorBody);

    [LoggerMessage(
        EventId = 6701,
        Level = LogLevel.Warning,
        Message = "Generation provider request timed out: Provider={Provider}")]
    public static partial void ProviderTimeout(ILogger logger, string provider);

    [LoggerMessage(
        EventId = 6702,
        Level = LogLevel.Warning,
        Message = "Generation provider request failed (network/transport): Provider={Provider}")]
    public static partial void ProviderRequestFailed(ILogger logger, string provider, Exception exception);

    [LoggerMessage(
        EventId = 6703,
        Level = LogLevel.Warning,
        Message = "Generation provider response could not be parsed: Provider={Provider}")]
    public static partial void ProviderResponseParseFailed(ILogger logger, string provider, Exception exception);
}
