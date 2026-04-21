// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Microsoft.Extensions.Logging;

namespace Honua.Postgres.Features.Import;

/// <summary>
/// Refactored file import service that follows SOLID principles.
/// Uses composition instead of implementing all responsibilities directly.
/// Replaces the original StreamingFileImportService god class.
/// </summary>
internal sealed class RefactoredStreamingFileImportService : IFileImportService
{
    private readonly IFileFormatDetectionService _formatDetectionService;
    private readonly IFilePreviewService _previewService;
    private readonly IStreamingImportProcessor _importProcessor;
    private readonly ILogger<RefactoredStreamingFileImportService> _logger;

    public RefactoredStreamingFileImportService(
        IFileFormatDetectionService formatDetectionService,
        IFilePreviewService previewService,
        IStreamingImportProcessor importProcessor,
        ILogger<RefactoredStreamingFileImportService> logger)
    {
        _formatDetectionService = formatDetectionService;
        _previewService = previewService;
        _importProcessor = importProcessor;
        _logger = logger;
    }

    /// <summary>
    /// Delegates format detection to specialized service
    /// </summary>
    public SupportedFileFormat? DetectFormat(string fileName)
    {
        return _formatDetectionService.DetectFormat(fileName);
    }

    /// <summary>
    /// Delegates supported extensions query to specialized service
    /// </summary>
    public string[] GetSupportedExtensions()
    {
        return _formatDetectionService.GetSupportedExtensions();
    }

    /// <summary>
    /// Delegates import processing to specialized service
    /// </summary>
    public Task<ImportResult> ImportFileAsync(
        ImportRequest request,
        CancellationToken cancellationToken = default)
    {
        return _importProcessor.ProcessImportAsync(request, cancellationToken);
    }

    /// <summary>
    /// Delegates import processing with progress to specialized service
    /// </summary>
    public Task<ImportResult> ImportFileAsync(
        ImportRequest request,
        IProgress<ImportProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        return _importProcessor.ProcessImportAsync(request, progress, cancellationToken);
    }

    /// <summary>
    /// Delegates file preview to specialized service
    /// </summary>
    public Task<FilePreview> PreviewFileAsync(
        Stream fileStream,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        return _previewService.PreviewFileAsync(fileStream, fileName, cancellationToken);
    }

    /// <summary>
    /// Gets import limits from the import processor
    /// </summary>
    public ImportLimits Limits => _importProcessor.Limits;
}