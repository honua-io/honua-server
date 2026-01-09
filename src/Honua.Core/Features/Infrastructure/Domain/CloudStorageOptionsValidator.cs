// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;
using Honua.Core.Configuration;

namespace Honua.Core.Features.Infrastructure.Domain;

/// <summary>
/// Validates CloudStorageOptions configuration to ensure proper cloud storage setup.
/// Enforces provider-specific validation rules and security best practices.
/// </summary>
public sealed class CloudStorageOptionsValidator : OptionsValidator<CloudStorageOptions>
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
    /// <param name="failures">List to add validation errors to</param>
    protected override void ValidateOptions(CloudStorageOptions options, List<string> failures)
    {
        // Complex business rule validations
        ValidateGeneralSettings(options, failures);
        ValidateProviderSpecificSettings(options, failures);
    }


    /// <summary>
    /// Validates general storage settings.
    /// </summary>
    private static void ValidateGeneralSettings(CloudStorageOptions options, List<string> failures)
    {
        // Validate DefaultTimeToLive
        ValidateTimeSpan(options.DefaultTimeToLive, TimeSpan.FromMinutes(1), TimeSpan.FromDays(365), "DefaultTimeToLive", failures);

        // Validate MaxFileSizeBytes (1MB to 10GB)
        ValidateFileSize(options.MaxFileSizeBytes, 1024 * 1024, 10L * 1024L * 1024L * 1024L, "MaxFileSizeBytes", failures);

        // Validate cleanup settings
        if (options.EnableAutomaticCleanup)
        {
            ValidateTimeSpan(options.CleanupInterval, TimeSpan.FromMinutes(15), TimeSpan.FromDays(1), "CleanupInterval", failures);
        }
    }

    /// <summary>
    /// Validates provider-specific configuration.
    /// </summary>
    private static void ValidateProviderSpecificSettings(CloudStorageOptions options, List<string> failures)
    {
        switch (options.Provider)
        {
            case CloudStorageProvider.Local:
                ValidateLocalStorageOptions(options.LocalStorage, failures);
                break;
            case CloudStorageProvider.AwsS3:
                ValidateAwsS3Options(options.AwsS3, failures);
                break;
            case CloudStorageProvider.AzureBlob:
                ValidateAzureBlobOptions(options.AzureBlob, failures);
                break;
            case CloudStorageProvider.GoogleCloudStorage:
                ValidateGoogleCloudStorageOptions(options.GoogleCloudStorage, failures);
                break;
            default:
                failures.Add($"Unsupported cloud storage provider: {options.Provider}");
                break;
        }
    }

    /// <summary>
    /// Validates local storage specific options.
    /// </summary>
    private static void ValidateLocalStorageOptions(LocalStorageOptions? options, List<string> failures)
    {
        if (options == null)
        {
            failures.Add("LocalStorage configuration is required when Provider is Local");
            return;
        }

        ValidateRequiredString(options.BasePath, "LocalStorage.BasePath", failures);

        if (!string.IsNullOrWhiteSpace(options.BasePath))
        {
            ValidatePath(options.BasePath, "LocalStorage.BasePath", failures, requireAbsolute: true, preventTraversal: true);
        }
    }

    /// <summary>
    /// Validates AWS S3 specific options.
    /// </summary>
    private static void ValidateAwsS3Options(AwsS3Options? options, List<string> failures)
    {
        if (options == null)
        {
            failures.Add("AwsS3 configuration is required when Provider is AwsS3");
            return;
        }

        // Bucket name validation
        ValidateRequiredString(options.BucketName, "AwsS3.BucketName", failures);
        if (!string.IsNullOrWhiteSpace(options.BucketName))
        {
            ValidateStringLength(options.BucketName, 63, "AwsS3.BucketName", failures, 3);

            if (!_s3BucketNamePattern.IsMatch(options.BucketName))
            {
                failures.Add("AwsS3.BucketName must contain only lowercase letters, numbers, and hyphens, and cannot start or end with a hyphen");
            }
        }

        // Region validation
        ValidateRequiredString(options.Region, "AwsS3.Region", failures);

        // Validate credentials if provided
        if (!string.IsNullOrEmpty(options.AccessKeyId) && string.IsNullOrEmpty(options.SecretAccessKey))
        {
            failures.Add("AwsS3.SecretAccessKey must be provided when AccessKeyId is specified");
        }

        if (!string.IsNullOrEmpty(options.SecretAccessKey) && string.IsNullOrEmpty(options.AccessKeyId))
        {
            failures.Add("AwsS3.AccessKeyId must be provided when SecretAccessKey is specified");
        }

        // Validate ServiceUrl if provided (for S3-compatible endpoints)
        if (!string.IsNullOrEmpty(options.ServiceUrl))
        {
            ValidateUrl(options.ServiceUrl, "AwsS3.ServiceUrl", failures, requireHttps: false);
        }

        // Validate key prefix if provided
        if (!string.IsNullOrEmpty(options.KeyPrefix))
        {
            ValidatePath(options.KeyPrefix, "AwsS3.KeyPrefix", failures, requireAbsolute: false, preventTraversal: false);
        }
    }

    /// <summary>
    /// Validates Azure Blob specific options.
    /// </summary>
    private static void ValidateAzureBlobOptions(AzureBlobOptions? options, List<string> failures)
    {
        if (options == null)
        {
            failures.Add("AzureBlob configuration is required when Provider is AzureBlob");
            return;
        }

        // Connection string validation
        ValidateRequiredString(options.ConnectionString, "AzureBlob.ConnectionString", failures);

        // Container name validation
        ValidateRequiredString(options.ContainerName, "AzureBlob.ContainerName", failures);
        if (!string.IsNullOrWhiteSpace(options.ContainerName))
        {
            ValidateStringLength(options.ContainerName, 63, "AzureBlob.ContainerName", failures, 3);

            if (!_azureContainerNamePattern.IsMatch(options.ContainerName))
            {
                failures.Add("AzureBlob.ContainerName must contain only lowercase letters, numbers, and hyphens, and cannot start or end with a hyphen");
            }
        }

        // Validate blob prefix if provided
        if (!string.IsNullOrEmpty(options.BlobPrefix))
        {
            ValidatePath(options.BlobPrefix, "AzureBlob.BlobPrefix", failures, requireAbsolute: false, preventTraversal: false);
        }
    }

    /// <summary>
    /// Validates Google Cloud Storage specific options.
    /// </summary>
    private static void ValidateGoogleCloudStorageOptions(GoogleCloudStorageOptions? options, List<string> failures)
    {
        if (options == null)
        {
            failures.Add("GoogleCloudStorage configuration is required when Provider is GoogleCloudStorage");
            return;
        }

        // Bucket name validation
        ValidateRequiredString(options.BucketName, "GoogleCloudStorage.BucketName", failures);
        if (!string.IsNullOrWhiteSpace(options.BucketName))
        {
            ValidateStringLength(options.BucketName, 63, "GoogleCloudStorage.BucketName", failures, 3);

            // GCS bucket naming is similar to S3 but with additional restrictions
            if (!_s3BucketNamePattern.IsMatch(options.BucketName))
            {
                failures.Add("GoogleCloudStorage.BucketName must contain only lowercase letters, numbers, and hyphens, and cannot start or end with a hyphen");
            }
        }

        // Project ID validation
        ValidateRequiredString(options.ProjectId, "GoogleCloudStorage.ProjectId", failures);

        // Validate credentials path if provided
        if (!string.IsNullOrEmpty(options.CredentialsPath))
        {
            ValidatePath(options.CredentialsPath, "GoogleCloudStorage.CredentialsPath", failures);

            if (!File.Exists(options.CredentialsPath))
            {
                failures.Add($"GoogleCloudStorage.CredentialsPath file does not exist: {options.CredentialsPath}");
            }
            else if (!options.CredentialsPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add("GoogleCloudStorage.CredentialsPath should point to a JSON credentials file");
            }
        }

        // Validate object prefix if provided
        if (!string.IsNullOrEmpty(options.ObjectPrefix))
        {
            ValidatePath(options.ObjectPrefix, "GoogleCloudStorage.ObjectPrefix", failures, requireAbsolute: false, preventTraversal: false);
        }
    }
}
