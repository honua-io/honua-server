// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Infrastructure.Domain;

/// <summary>
/// Supported cloud storage providers for file storage operations
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CloudStorageProvider>))]
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
    AzureBlob = 2
}
