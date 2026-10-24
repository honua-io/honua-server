// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Security;

/// <summary>
/// Configuration options for file upload security scanning.
/// </summary>
internal sealed class FileUploadSecurityOptions
{
    /// <summary>
    /// Configuration section name for binding.
    /// </summary>
    public const string SectionName = "FileUploadSecurity";

    /// <summary>
    /// Maximum number of prefix bytes to inspect for binary signatures before text-format deep scanning.
    /// </summary>
    public long MaxSecurityScanSizeBytes { get; set; } = FileUploadSecurity.MaxSecurityScanSize;
}
