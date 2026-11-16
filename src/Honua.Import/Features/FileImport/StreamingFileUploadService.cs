// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Import.FileImport;

internal sealed class StreamingFileUploadService : IUploadQueueMetricsProvider
{
    private readonly FileUploadOptions _options;

    public StreamingFileUploadService(IOptions<FileUploadOptions> options)
    {
        _options = options.Value;
    }

    public UploadQueueSnapshot GetQueueSnapshot()
    {
        // No in-process upload queue remains (#2459): depth and active counts are always zero;
        // the configured limits are still reported so dashboards keep their capacity context.
        return new UploadQueueSnapshot(
            0,
            _options.MaxQueuedUploads,
            0,
            _options.MaxConcurrentUploads);
    }
}

internal sealed class FileUploadOptions
{
    public const string SectionName = "FileUpload";

    public long MaxFileSizeBytes { get; set; } = 100 * 1024 * 1024;

    public int MaxConcurrentUploads { get; set; } = 5;

    public int MaxQueuedUploads { get; set; } = 20;
}
