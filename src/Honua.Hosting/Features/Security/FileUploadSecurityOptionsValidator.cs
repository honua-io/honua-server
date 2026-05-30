// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using Honua.Core.Configuration;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Security;

/// <summary>
/// Validates FileUploadSecurityOptions configuration to ensure secure file upload handling.
/// Enforces reasonable limits and security best practices for file uploads.
/// </summary>
internal sealed class FileUploadSecurityOptionsValidator : OptionsValidator<FileUploadSecurityOptions>
{
    /// <summary>
    /// Validates the file upload security options configuration using derived class-specific logic.
    /// </summary>
    /// <param name="options">The file upload security options instance to validate</param>
    /// <param name="failures">List to add validation errors to</param>
    protected override void ValidateOptions(FileUploadSecurityOptions options, List<string> failures)
    {
        // Complex business rule validations
        ValidateSecurityScanSize(options, failures);
    }


    /// <summary>
    /// Validates security scan size configuration.
    /// </summary>
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
