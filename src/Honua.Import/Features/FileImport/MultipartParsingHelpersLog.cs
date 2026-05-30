// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Server.Features.Import;
using Honua.Server.Features.Migration;
using Honua.Server.Features.FileImport;
using Honua.Server.Features.RasterImport;
namespace Honua.Server.Features.FileImport;

internal static partial class MultipartParsingHelpersLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Best-effort cleanup of staged file failed: {Path}")]
    public static partial void StagedFileCleanupFailed(ILogger logger, string path, Exception exception);
}
