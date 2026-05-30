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

namespace Honua.Core.Features.FileImport.Abstractions;

/// <summary>
/// Service responsible for previewing file contents without importing.
/// Segregated interface following the Single Responsibility and Interface Segregation principles.
/// </summary>
public interface IFilePreviewService
{
    /// <summary>
    /// Preview file contents without importing (first N features based on limits).
    /// Uses streaming to avoid loading entire file into memory.
    /// </summary>
    /// <param name="fileStream">File stream to preview</param>
    /// <param name="fileName">Original filename for format detection</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Preview information including feature count and sample features</returns>
    Task<FilePreview> PreviewFileAsync(
        Stream fileStream,
        string fileName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Preview file contents with custom limits
    /// </summary>
    /// <param name="fileStream">File stream to preview</param>
    /// <param name="fileName">Original filename for format detection</param>
    /// <param name="previewLimits">Custom limits for preview</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Preview information including feature count and sample features</returns>
    Task<FilePreview> PreviewFileAsync(
        Stream fileStream,
        string fileName,
        ImportLimits previewLimits,
        CancellationToken cancellationToken = default);
}
