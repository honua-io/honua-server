// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Microsoft.Extensions.Logging;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.FileImport.Services.FileGdb;

namespace Honua.Core.Features.FileImport.Services;

internal static partial class FileFormatDetectionLog
{
    [LoggerMessage(EventId = 8750, Level = LogLevel.Debug, Message = "Detected format {Format} from extension {Extension} for file {FileName}")]
    public static partial void DetectedFromExtension(ILogger logger, SupportedFileFormat format, string extension, string fileName);

    [LoggerMessage(EventId = 8751, Level = LogLevel.Debug, Message = "Detected format {Format} from magic number for file {FileName}")]
    public static partial void DetectedFromMagicNumber(ILogger logger, SupportedFileFormat format, string fileName);

    [LoggerMessage(EventId = 8752, Level = LogLevel.Debug, Message = "Detected format {Format} from content analysis for file {FileName}")]
    public static partial void DetectedFromContentAnalysis(ILogger logger, SupportedFileFormat format, string fileName);
}
