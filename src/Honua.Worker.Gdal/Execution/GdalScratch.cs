// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Shared scratch-workspace cleanup helper. Every native-profile executor
/// allocates a per-job subdirectory under <c>GdalWorkerOptions.ScratchRoot</c>
/// and must delete it after execution; the cleanup is best-effort and structured
/// so transient filesystem failures do not surface as job failures.
/// </summary>
internal static partial class GdalScratch
{
    /// <summary>
    /// Best-effort recursive delete of a per-job scratch workspace. Logs and
    /// swallows IO/permission errors so the executor's finally block never throws.
    /// </summary>
    public static void TryCleanup<T>(string workspace, ILogger<T> logger)
    {
        try
        {
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
        catch (IOException ex)
        {
            Log.CleanupFailed(logger, workspace, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.CleanupFailed(logger, workspace, ex.Message);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(9260, LogLevel.Warning,
            "GDAL worker scratch cleanup failed for {Workspace}: {Reason}")]
        public static partial void CleanupFailed(ILogger logger, string workspace, string reason);
    }
}
