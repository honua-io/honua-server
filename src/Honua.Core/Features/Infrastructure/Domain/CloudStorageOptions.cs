// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Tiles.PMTiles;

namespace Honua.Core.Features.Infrastructure.Domain;

public sealed record CloudStorageOptions
{
    public bool Enabled { get; set; } = true;

    public CloudStorageProvider Provider { get; set; } = CloudStorageProvider.Local;

    public TimeSpan DefaultTimeToLive { get; set; } = TimeSpan.FromHours(24);

    public long MaxFileSizeBytes { get; set; } = 1024L * 1024L * 1024L; // 1 GB default

    public LocalStorageOptions? LocalStorage { get; set; }

    public AwsS3Options? AwsS3 { get; set; }

    public AzureBlobOptions? AzureBlob { get; set; }

    public TimeSpan SignedUrlLifetime { get; set; } = TimeSpan.FromMinutes(15);

    public bool EnableAutomaticCleanup { get; set; } = true;

    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);

    public PMTilesPublishOptions PMTilesPublish { get; set; } = new();
}

public sealed record PMTilesPublishOptions
{
    public PMTilesUrlStrategy UrlStrategy { get; set; } = PMTilesUrlStrategy.SignedUrl;

    public string KeyPrefix { get; set; } = "pmtiles";

    public TimeSpan SignedUrlLifetime { get; set; } = TimeSpan.FromDays(7);

    public string? PublicBucketBaseUrl { get; set; }

    public bool AllowAnonymousUnboundArchives { get; set; }
}

public sealed record LocalStorageOptions
{
    public string BasePath { get; set; } = string.Empty;

    public bool CreateDirectoryIfNotExists { get; set; } = true;
}

public sealed record AwsS3Options
{
    public string BucketName { get; set; } = string.Empty;

    public string Region { get; set; } = string.Empty;

    public string? KeyPrefix { get; set; }

    public string? ServiceUrl { get; set; }

    public bool ForcePathStyle { get; set; }

    public string? AccessKeyId { get; set; }

    public string? SecretAccessKey { get; set; }

    public bool EnableServerSideEncryption { get; set; } = true;
}

public sealed record AzureBlobOptions
{
    public string ConnectionString { get; set; } = string.Empty;

    public string ContainerName { get; set; } = string.Empty;

    public string? BlobPrefix { get; set; }
}
