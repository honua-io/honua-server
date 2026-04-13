// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.ServiceRegistration;

namespace Honua.Core.Features.Infrastructure.Domain;

/// <summary>
/// Validates CloudStorageOptions configuration to ensure proper cloud storage setup.
/// Enforces provider-specific validation rules and security best practices.
/// </summary>
public sealed class CloudStorageOptionsValidator : ConfigurationValidator<CloudStorageOptions>
{
    /// <summary>
    /// AWS S3 bucket name validation pattern.
    /// Based on AWS S3 bucket naming rules.
    /// </summary>
    private static readonly Regex _s3BucketNamePattern = new(
        @"^[a-z0-9][a-z0-9\-]*[a-z0-9]$|^[a-z0-9]$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Azure container name validation pattern.
    /// Based on Azure Blob Storage container naming rules.
    /// </summary>
    private static readonly Regex _azureContainerNamePattern = new(
        @"^[a-z0-9][a-z0-9\-]*[a-z0-9]$|^[a-z0-9]$",
        RegexOptions.Compiled);

    /// <summary>
    /// Validates the cloud storage options configuration using derived class-specific logic.
    /// </summary>
    /// <param name="options">The cloud storage options instance to validate</param>
    /// <param name="errors">List to add validation errors to</param>
    protected override void PerformFeatureSpecificValidation(CloudStorageOptions options, List<string> errors)
    {
        // Complex business rule validations
        ValidateGeneralSettings(options, errors);
        ValidateProviderSpecificSettings(options, errors);
    }


    /// <summary>
    /// Validates general storage settings.
    /// </summary>
    private static void ValidateGeneralSettings(CloudStorageOptions options, List<string> errors)
    {
        // Validate DefaultTimeToLive
        ValidateTimeSpan(options.DefaultTimeToLive, TimeSpan.FromMinutes(1), TimeSpan.FromDays(365), "DefaultTimeToLive", errors);

        // Validate MaxFileSizeBytes (1MB to 10GB)
        ValidateFileSize(options.MaxFileSizeBytes, 1024 * 1024, 10L * 1024L * 1024L * 1024L, "MaxFileSizeBytes", errors);

        // Validate cleanup settings
        if (options.EnableAutomaticCleanup)
        {
            ValidateTimeSpan(options.CleanupInterval, TimeSpan.FromMinutes(15), TimeSpan.FromDays(1), "CleanupInterval", errors);
        }
    }

    /// <summary>
    /// Validates provider-specific configuration.
    /// </summary>
    private static void ValidateProviderSpecificSettings(CloudStorageOptions options, List<string> errors)
    {
        switch (options.Provider)
        {
            case CloudStorageProvider.Local:
                ValidateLocalStorageOptions(options.LocalStorage, errors);
                break;
            case CloudStorageProvider.AwsS3:
                ValidateAwsS3Options(options.AwsS3, errors);
                break;
            case CloudStorageProvider.AzureBlob:
                ValidateAzureBlobOptions(options.AzureBlob, errors);
                break;
            default:
                errors.Add($"Unsupported cloud storage provider: {options.Provider}");
                break;
        }
    }

    /// <summary>
    /// Validates local storage specific options.
    /// </summary>
    private static void ValidateLocalStorageOptions(LocalStorageOptions? options, List<string> errors)
    {
        if (options == null)
        {
            errors.Add("LocalStorage configuration is required when Provider is Local");
            return;
        }

        ValidateRequiredString(options.BasePath, "LocalStorage.BasePath", errors);

        if (!string.IsNullOrWhiteSpace(options.BasePath))
        {
            ValidatePath(options.BasePath, "LocalStorage.BasePath", errors, requireAbsolute: true, preventTraversal: true);
        }
    }

    /// <summary>
    /// Validates AWS S3 specific options.
    /// </summary>
    private static void ValidateAwsS3Options(AwsS3Options? options, List<string> errors)
    {
        if (options == null)
        {
            errors.Add("AwsS3 configuration is required when Provider is AwsS3");
            return;
        }

        // Bucket name validation
        ValidateRequiredString(options.BucketName, "AwsS3.BucketName", errors);
        if (!string.IsNullOrWhiteSpace(options.BucketName))
        {
            ValidateStringLength(options.BucketName, 63, "AwsS3.BucketName", errors, 3);

            if (!_s3BucketNamePattern.IsMatch(options.BucketName))
            {
                errors.Add("AwsS3.BucketName must contain only lowercase letters, numbers, and hyphens, and cannot start or end with a hyphen");
            }
        }

        // Region validation
        ValidateRequiredString(options.Region, "AwsS3.Region", errors);

        // Validate credentials if provided
        if (!string.IsNullOrEmpty(options.AccessKeyId) && string.IsNullOrEmpty(options.SecretAccessKey))
        {
            errors.Add("AwsS3.SecretAccessKey must be provided when AccessKeyId is specified");
        }

        if (!string.IsNullOrEmpty(options.SecretAccessKey) && string.IsNullOrEmpty(options.AccessKeyId))
        {
            errors.Add("AwsS3.AccessKeyId must be provided when SecretAccessKey is specified");
        }

        // Validate ServiceUrl if provided (for S3-compatible endpoints)
        if (!string.IsNullOrEmpty(options.ServiceUrl))
        {
            ValidateUrl(options.ServiceUrl, "AwsS3.ServiceUrl", errors, requireHttps: false);
        }

        // Validate key prefix if provided
        if (!string.IsNullOrEmpty(options.KeyPrefix))
        {
            ValidatePath(options.KeyPrefix, "AwsS3.KeyPrefix", errors, requireAbsolute: false, preventTraversal: false);
        }
    }

    /// <summary>
    /// Validates Azure Blob specific options.
    /// </summary>
    private static void ValidateAzureBlobOptions(AzureBlobOptions? options, List<string> errors)
    {
        if (options == null)
        {
            errors.Add("AzureBlob configuration is required when Provider is AzureBlob");
            return;
        }

        // Connection string validation
        ValidateRequiredString(options.ConnectionString, "AzureBlob.ConnectionString", errors);

        // Container name validation
        ValidateRequiredString(options.ContainerName, "AzureBlob.ContainerName", errors);
        if (!string.IsNullOrWhiteSpace(options.ContainerName))
        {
            ValidateStringLength(options.ContainerName, 63, "AzureBlob.ContainerName", errors, 3);

            if (!_azureContainerNamePattern.IsMatch(options.ContainerName))
            {
                errors.Add("AzureBlob.ContainerName must contain only lowercase letters, numbers, and hyphens, and cannot start or end with a hyphen");
            }
        }

        // Validate blob prefix if provided
        if (!string.IsNullOrEmpty(options.BlobPrefix))
        {
            ValidatePath(options.BlobPrefix, "AzureBlob.BlobPrefix", errors, requireAbsolute: false, preventTraversal: false);
        }
    }

}
