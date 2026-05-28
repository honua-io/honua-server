// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.FileImport.Services.FileGdb;

namespace Honua.Core.Features.FileImport.Abstractions;

/// <summary>
/// Service responsible for detecting file formats from filenames and content.
/// Segregated interface following the Single Responsibility and Interface Segregation principles.
/// </summary>
public interface IFileFormatDetectionService
{
    /// <summary>
    /// Detect file format from filename extension
    /// </summary>
    /// <param name="fileName">Original filename</param>
    /// <returns>Detected file format or null if not supported</returns>
    SupportedFileFormat? DetectFormat(string fileName);

    /// <summary>
    /// Get supported file extensions
    /// </summary>
    /// <returns>Array of supported extensions (with dots)</returns>
    string[] GetSupportedExtensions();

    /// <summary>
    /// Detect file format from content analysis
    /// </summary>
    /// <param name="fileContent">File content to analyze</param>
    /// <param name="fileName">Original filename for context</param>
    /// <returns>Detected file format or null if not supported</returns>
    SupportedFileFormat? DetectFormatFromContent(ReadOnlySpan<byte> fileContent, string fileName);
}
