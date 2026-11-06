// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using Honua.Core.Configuration;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Security;

internal sealed class FileUploadSecurityOptionsValidator : OptionsValidator<FileUploadSecurityOptions>
{
    protected override void ValidateOptions(FileUploadSecurityOptions options, List<string> failures)
    {
        // Complex business rule validations
        ValidateSecurityScanSize(options, failures);
    }


    private static void ValidateSecurityScanSize(FileUploadSecurityOptions options, List<string> failures)
    {
        // Basic range validation (1KB to 100MB)
        ValidateFileSize(options.MaxSecurityScanSizeBytes, 1024, 100 * 1024 * 1024, "MaxSecurityScanSizeBytes", failures);

        // Warn if significantly larger than default - may impact performance
        if (options.MaxSecurityScanSizeBytes > FileUploadSecurity.MaxSecurityScanSize * 2)
        {
            failures.Add($"MaxSecurityScanSizeBytes ({options.MaxSecurityScanSizeBytes:N0}) is significantly larger than recommended default ({FileUploadSecurity.MaxSecurityScanSize:N0}) and may impact performance");
        }

        // Recommended range validation for performance
        var minRecommended = 512 * 1024; // 512KB minimum for most file headers
        var maxRecommended = 50 * 1024 * 1024; // 50MB maximum for performance

        if (options.MaxSecurityScanSizeBytes < minRecommended)
        {
            failures.Add($"MaxSecurityScanSizeBytes ({options.MaxSecurityScanSizeBytes:N0}) is below recommended minimum ({minRecommended:N0}) and may miss malicious content in larger files");
        }

        if (options.MaxSecurityScanSizeBytes > maxRecommended)
        {
            failures.Add($"MaxSecurityScanSizeBytes ({options.MaxSecurityScanSizeBytes:N0}) exceeds recommended maximum ({maxRecommended:N0}) and may cause performance issues during security scanning");
        }
    }
}
