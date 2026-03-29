// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.PrintingTools;

/// <summary>
/// Source-generated log messages for PrintingTools operations.
/// EventId range: 5500-5549 (PrintingTools)
/// </summary>
internal static partial class PrintingToolsLog
{
    /// <summary>
    /// Logs when a synchronous print execute request is received.
    /// </summary>
    [LoggerMessage(
        EventId = 5500,
        Level = LogLevel.Information,
        Message = "Print execute requested: template={TemplateName} format={Format} dpi={Dpi}")]
    public static partial void ExecuteRequested(ILogger logger, string templateName, string format, int dpi);

    /// <summary>
    /// Logs when a print execute completes successfully.
    /// </summary>
    [LoggerMessage(
        EventId = 5501,
        Level = LogLevel.Information,
        Message = "Print execute completed: template={TemplateName} format={Format} size={OutputBytes} bytes in {ElapsedMs}ms")]
    public static partial void ExecuteCompleted(ILogger logger, string templateName, string format, long outputBytes, double elapsedMs);

    /// <summary>
    /// Logs when a print execute fails.
    /// </summary>
    [LoggerMessage(
        EventId = 5502,
        Level = LogLevel.Error,
        Message = "Print execute failed: template={TemplateName} format={Format}: {ErrorMessage}")]
    public static partial void ExecuteFailed(ILogger logger, string templateName, string format, string errorMessage, Exception? exception = null);

    /// <summary>
    /// Logs when an async print job is submitted.
    /// </summary>
    [LoggerMessage(
        EventId = 5510,
        Level = LogLevel.Information,
        Message = "Print job submitted: jobId={JobId} template={TemplateName} format={Format}")]
    public static partial void JobSubmitted(ILogger logger, string jobId, string templateName, string format);

    /// <summary>
    /// Logs when an async print job completes.
    /// </summary>
    [LoggerMessage(
        EventId = 5511,
        Level = LogLevel.Information,
        Message = "Print job completed: jobId={JobId} size={OutputBytes} bytes")]
    public static partial void JobCompleted(ILogger logger, string jobId, long outputBytes);

    /// <summary>
    /// Logs when an async print job fails.
    /// </summary>
    [LoggerMessage(
        EventId = 5512,
        Level = LogLevel.Error,
        Message = "Print job failed: jobId={JobId}: {ErrorMessage}")]
    public static partial void JobFailed(ILogger logger, string jobId, string errorMessage, Exception? exception = null);

    /// <summary>
    /// Logs when a job status query is received.
    /// </summary>
    [LoggerMessage(
        EventId = 5520,
        Level = LogLevel.Debug,
        Message = "Print job status queried: jobId={JobId}")]
    public static partial void JobStatusQueried(ILogger logger, string jobId);

    /// <summary>
    /// Logs when edition gating blocks a print request.
    /// </summary>
    [LoggerMessage(
        EventId = 5530,
        Level = LogLevel.Warning,
        Message = "Print request blocked by edition gating: feature={Feature} requires Pro edition")]
    public static partial void EditionGateBlocked(ILogger logger, string feature);

    /// <summary>
    /// Logs when an async print job is cancelled.
    /// </summary>
    [LoggerMessage(
        EventId = 5515,
        Level = LogLevel.Information,
        Message = "Print job cancelled: jobId={JobId}")]
    public static partial void JobCancelled(ILogger logger, string jobId);

    /// <summary>
    /// Logs when persisting error status for a failed print job also fails.
    /// </summary>
    [LoggerMessage(
        EventId = 5513,
        Level = LogLevel.Warning,
        Message = "Failed to persist error status for print job {JobId} (original error: {OriginalError})")]
    public static partial void JobProgressPersistFailed(ILogger logger, string jobId, string originalError, Exception? exception = null);

    /// <summary>
    /// Logs when persisting cancelled status for a print job fails.
    /// </summary>
    [LoggerMessage(
        EventId = 5514,
        Level = LogLevel.Warning,
        Message = "Failed to persist cancelled status for print job {JobId}")]
    public static partial void JobCancelPersistFailed(ILogger logger, string jobId, Exception? exception = null);

    /// <summary>
    /// Logs when a web map definition contains a baseMap that cannot be rendered.
    /// </summary>
    [LoggerMessage(
        EventId = 5531,
        Level = LogLevel.Warning,
        Message = "Web map definition contains a baseMap element which is not yet supported and will be omitted from the output")]
    public static partial void BaseMapNotSupported(ILogger logger);
}
