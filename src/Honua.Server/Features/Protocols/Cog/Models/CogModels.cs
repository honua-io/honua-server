// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Server.Features.Protocols.Cog.Models;

internal sealed record RegisterCogRequest
{
    public int LayerId { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public CloudStorageProvider Provider { get; init; }

    public string Bucket { get; init; } = string.Empty;

    public string ObjectKey { get; init; } = string.Empty;

    public bool IsValid(out string error)
    {
        if (LayerId <= 0) { error = "LayerId must be a positive integer."; return false; }
        if (string.IsNullOrWhiteSpace(Name)) { error = "Name is required."; return false; }
        if (string.IsNullOrWhiteSpace(Bucket)) { error = "Bucket is required."; return false; }
        if (string.IsNullOrWhiteSpace(ObjectKey)) { error = "ObjectKey is required."; return false; }
        if (!Enum.IsDefined(Provider)) { error = "Provider must be one of: AwsS3, AzureBlob."; return false; }
        if (Provider == CloudStorageProvider.Local) { error = "Local storage is not supported for COG serving."; return false; }
        error = string.Empty;
        return true;
    }
}

internal sealed record CogRegistrationResponse
{
    public long Id { get; init; }

    public int LayerId { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public string Provider { get; init; } = string.Empty;

    public string Bucket { get; init; } = string.Empty;

    public string ObjectKey { get; init; } = string.Empty;

    public int? Width { get; init; }

    public int? Height { get; init; }

    public int? BandCount { get; init; }

    public int? Srid { get; init; }

    public string? Compression { get; init; }

    public int? OverviewLevelCount { get; init; }

    public DateTimeOffset? MetadataScannedAt { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
