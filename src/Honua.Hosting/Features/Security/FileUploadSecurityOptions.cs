// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Security;

internal sealed class FileUploadSecurityOptions
{
    public const string SectionName = "FileUploadSecurity";

    public long MaxSecurityScanSizeBytes { get; set; } = FileUploadSecurity.MaxSecurityScanSize;
}
