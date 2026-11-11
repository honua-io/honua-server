// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Io.Export;

internal static partial class ExportJobServiceLog
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to delete artifact {FileId} for export job {JobId}; preserving the accepted job state.")]
    public static partial void ArtifactCleanupFailed(ILogger logger, string jobId, string fileId, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Export job {JobId} was accepted but could not be queued; the reconciler's dispatch repair will queue it.")]
    public static partial void EnqueueDeferredToDispatchRepair(ILogger logger, string jobId, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to record the terminal status of export job {JobId} on its progress.")]
    public static partial void TerminalProgressFailed(ILogger logger, string jobId, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to delete scratch directory {ScratchDir} for export job {JobId}; leaking temp files.")]
    public static partial void ScratchDirCleanupFailed(ILogger logger, string jobId, string scratchDir, Exception exception);
}
