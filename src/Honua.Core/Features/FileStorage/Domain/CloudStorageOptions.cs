// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.FileStorage.Domain;

/// <summary>
/// Configuration options for cloud file storage
/// </summary>
public sealed record CloudStorageOptions
{
    /// <summary>
    /// The cloud storage provider to use
    /// </summary>
    public CloudStorageProvider Provider { get; init; } = CloudStorageProvider.Local;

    /// <summary>
    /// Default time-to-live for temporary files
    /// </summary>
    public TimeSpan DefaultTimeToLive { get; init; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Maximum file size allowed for upload (in bytes)
    /// </summary>
    public long MaxFileSizeBytes { get; init; } = 1024L * 1024L * 1024L; // 1 GB default

    /// <summary>
    /// Options specific to local filesystem storage
    /// </summary>
    public LocalStorageOptions? LocalStorage { get; init; }

    /// <summary>
    /// Options specific to AWS S3 storage
    /// </summary>
    public AwsS3Options? AwsS3 { get; init; }

    /// <summary>
    /// Options specific to Azure Blob storage
    /// </summary>
    public AzureBlobOptions? AzureBlob { get; init; }

    /// <summary>
    /// Options specific to Google Cloud Storage
    /// </summary>
    public GoogleCloudStorageOptions? GoogleCloudStorage { get; init; }

    /// <summary>
    /// Whether to enable automatic cleanup of expired files
    /// </summary>
    public bool EnableAutomaticCleanup { get; init; } = true;

    /// <summary>
    /// Interval at which to run automatic cleanup
    /// </summary>
    public TimeSpan CleanupInterval { get; init; } = TimeSpan.FromHours(1);
}

/// <summary>
/// Configuration options for local filesystem storage
/// </summary>
public sealed record LocalStorageOptions
{
    /// <summary>
    /// Base directory path for storing files
    /// </summary>
    public required string BasePath { get; init; }

    /// <summary>
    /// Whether to create the base directory if it doesn't exist
    /// </summary>
    public bool CreateDirectoryIfNotExists { get; init; } = true;
}

/// <summary>
/// Configuration options for AWS S3 storage
/// </summary>
public sealed record AwsS3Options
{
    /// <summary>
    /// S3 bucket name
    /// </summary>
    public required string BucketName { get; init; }

    /// <summary>
    /// AWS region (e.g., "us-east-1")
    /// </summary>
    public required string Region { get; init; }

    /// <summary>
    /// Optional key prefix for all stored files
    /// </summary>
    public string? KeyPrefix { get; init; }

    /// <summary>
    /// AWS access key ID (if not using IAM role)
    /// </summary>
    public string? AccessKeyId { get; init; }

    /// <summary>
    /// AWS secret access key (if not using IAM role)
    /// </summary>
    public string? SecretAccessKey { get; init; }

    /// <summary>
    /// Whether to use server-side encryption
    /// </summary>
    public bool EnableServerSideEncryption { get; init; } = true;
}

/// <summary>
/// Configuration options for Azure Blob storage
/// </summary>
public sealed record AzureBlobOptions
{
    /// <summary>
    /// Azure Storage connection string
    /// </summary>
    public required string ConnectionString { get; init; }

    /// <summary>
    /// Container name for storing files
    /// </summary>
    public required string ContainerName { get; init; }

    /// <summary>
    /// Optional blob prefix for all stored files
    /// </summary>
    public string? BlobPrefix { get; init; }
}

/// <summary>
/// Configuration options for Google Cloud Storage
/// </summary>
public sealed record GoogleCloudStorageOptions
{
    /// <summary>
    /// GCS bucket name
    /// </summary>
    public required string BucketName { get; init; }

    /// <summary>
    /// GCP project ID
    /// </summary>
    public required string ProjectId { get; init; }

    /// <summary>
    /// Optional object prefix for all stored files
    /// </summary>
    public string? ObjectPrefix { get; init; }

    /// <summary>
    /// Path to service account credentials JSON file (if not using default credentials)
    /// </summary>
    public string? CredentialsPath { get; init; }
}
