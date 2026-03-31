// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Domain;

/// <summary>
/// Configuration options for cloud file storage
/// </summary>
public sealed record CloudStorageOptions
{
    /// <summary>
    /// The cloud storage provider to use
    /// </summary>
    public CloudStorageProvider Provider { get; set; } = CloudStorageProvider.Local;

    /// <summary>
    /// Default time-to-live for temporary files
    /// </summary>
    public TimeSpan DefaultTimeToLive { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Maximum file size allowed for upload (in bytes)
    /// </summary>
    public long MaxFileSizeBytes { get; set; } = 1024L * 1024L * 1024L; // 1 GB default

    /// <summary>
    /// Options specific to local filesystem storage
    /// </summary>
    public LocalStorageOptions? LocalStorage { get; set; }

    /// <summary>
    /// Options specific to AWS S3 storage
    /// </summary>
    public AwsS3Options? AwsS3 { get; set; }

    /// <summary>
    /// Options specific to Azure Blob storage
    /// </summary>
    public AzureBlobOptions? AzureBlob { get; set; }

    /// <summary>
    /// Options specific to Google Cloud Storage
    /// </summary>
    public GoogleCloudStorageOptions? GoogleCloudStorage { get; set; }

    /// <summary>
    /// Lifetime for pre-signed download/upload URLs.
    /// Default: 15 minutes.
    /// </summary>
    public TimeSpan SignedUrlLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Whether to enable automatic cleanup of expired files
    /// </summary>
    public bool EnableAutomaticCleanup { get; set; } = true;

    /// <summary>
    /// Interval at which to run automatic cleanup
    /// </summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>
/// Configuration options for local filesystem storage
/// </summary>
public sealed record LocalStorageOptions
{
    /// <summary>
    /// Base directory path for storing files
    /// </summary>
    public string BasePath { get; set; } = string.Empty;

    /// <summary>
    /// Whether to create the base directory if it doesn't exist
    /// </summary>
    public bool CreateDirectoryIfNotExists { get; set; } = true;
}

/// <summary>
/// Configuration options for AWS S3 storage
/// </summary>
public sealed record AwsS3Options
{
    /// <summary>
    /// S3 bucket name
    /// </summary>
    public string BucketName { get; set; } = string.Empty;

    /// <summary>
    /// AWS region (e.g., "us-east-1")
    /// </summary>
    public string Region { get; set; } = string.Empty;

    /// <summary>
    /// Optional key prefix for all stored files
    /// </summary>
    public string? KeyPrefix { get; set; }

    /// <summary>
    /// Optional service URL for S3-compatible endpoints (e.g., Localstack or MinIO)
    /// </summary>
    public string? ServiceUrl { get; set; }

    /// <summary>
    /// Whether to force path-style S3 addressing (useful for local emulators)
    /// </summary>
    public bool ForcePathStyle { get; set; }

    /// <summary>
    /// AWS access key ID (if not using IAM role)
    /// </summary>
    public string? AccessKeyId { get; set; }

    /// <summary>
    /// AWS secret access key (if not using IAM role)
    /// </summary>
    public string? SecretAccessKey { get; set; }

    /// <summary>
    /// Whether to use server-side encryption
    /// </summary>
    public bool EnableServerSideEncryption { get; set; } = true;
}

/// <summary>
/// Configuration options for Azure Blob storage
/// </summary>
public sealed record AzureBlobOptions
{
    /// <summary>
    /// Azure Storage connection string
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Container name for storing files
    /// </summary>
    public string ContainerName { get; set; } = string.Empty;

    /// <summary>
    /// Optional blob prefix for all stored files
    /// </summary>
    public string? BlobPrefix { get; set; }
}

/// <summary>
/// Configuration options for Google Cloud Storage
/// </summary>
public sealed record GoogleCloudStorageOptions
{
    /// <summary>
    /// GCS bucket name
    /// </summary>
    public string BucketName { get; set; } = string.Empty;

    /// <summary>
    /// Google Cloud project ID
    /// </summary>
    public string? ProjectId { get; set; }

    /// <summary>
    /// Path to service account credential JSON file (if not using default credentials)
    /// </summary>
    public string? CredentialPath { get; set; }

    /// <summary>
    /// Optional key prefix for all stored files
    /// </summary>
    public string? KeyPrefix { get; set; }
}
