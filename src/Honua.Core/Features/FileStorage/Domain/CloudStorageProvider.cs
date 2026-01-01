// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.FileStorage.Domain;

/// <summary>
/// Supported cloud storage providers for file storage operations
/// </summary>
public enum CloudStorageProvider
{
    /// <summary>
    /// Local filesystem storage (for development and testing)
    /// </summary>
    Local = 0,

    /// <summary>
    /// Amazon Web Services S3
    /// </summary>
    AwsS3 = 1,

    /// <summary>
    /// Microsoft Azure Blob Storage
    /// </summary>
    AzureBlob = 2,

    /// <summary>
    /// Google Cloud Storage
    /// </summary>
    GoogleCloudStorage = 3
}
