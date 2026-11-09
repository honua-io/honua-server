// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.FileImport.Abstractions;

public interface IFileImportService
{
    SupportedFileFormat? DetectFormat(string fileName);

    string[] GetSupportedExtensions();

    Task<ImportResult> ImportFileAsync(ImportRequest request, CancellationToken cancellationToken = default);

    Task<ImportResult> ImportFileAsync(ImportRequest request, IProgress<ImportProgress>? progress, CancellationToken cancellationToken = default);

    Task<FilePreview> PreviewFileAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default);

    ImportLimits Limits { get; }
}


public sealed record FilePreview
{
    public required SupportedFileFormat Format { get; init; }

    public int TotalFeatureCount { get; init; }

    public int? DetectedSrid { get; init; }

    public Dictionary<string, object?> SampleProperties { get; init; } = new();

    public string[] AvailableLayers { get; init; } = Array.Empty<string>();

    public string[] Warnings { get; init; } = Array.Empty<string>();
}
