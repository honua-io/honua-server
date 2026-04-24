// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Import;

internal static partial class MultipartParsingHelpersLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Best-effort cleanup of staged file failed: {Path}")]
    public static partial void StagedFileCleanupFailed(ILogger logger, string path, Exception exception);
}
