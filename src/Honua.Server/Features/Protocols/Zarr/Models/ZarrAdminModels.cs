// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Server.Features.Protocols.Zarr.Models;

internal sealed record RegisterZarrRequest
{
    public int LayerId { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public CloudStorageProvider Provider { get; init; }

    public string Bucket { get; init; } = string.Empty;

    public string RootPath { get; init; } = string.Empty;

    public bool IsValid(out string error)
    {
        if (LayerId <= 0)
        {
            error = "LayerId must be a positive integer.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(Name))
        {
            error = "Name is required.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(Bucket))
        {
            error = "Bucket is required.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(RootPath))
        {
            error = "RootPath is required.";
            return false;
        }
        if (RootPath.Contains("..", StringComparison.Ordinal) ||
            RootPath.Contains('\\', StringComparison.Ordinal) ||
            RootPath.StartsWith('/'))
        {
            error = "RootPath must be a relative object key with no traversal sequences.";
            return false;
        }
        if (Bucket.Length > 255 || RootPath.Length > 1024)
        {
            error = "Bucket must be 255 characters or fewer and RootPath must be 1024 characters or fewer.";
            return false;
        }
        if (!Enum.IsDefined(Provider))
        {
            error = "Provider must be one of: AwsS3, AzureBlob, Local.";
            return false;
        }
        error = string.Empty;
        return true;
    }
}

internal sealed record ZarrRegistrationResponse
{
    public long Id { get; init; }

    public int LayerId { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public string Provider { get; init; } = string.Empty;

    public string Bucket { get; init; } = string.Empty;

    public string RootPath { get; init; } = string.Empty;

    public int? ZarrFormat { get; init; }

    public int? Srid { get; init; }

    public int? VariableCount { get; init; }

    public string? PrimaryVariable { get; init; }

    public ZarrVariableSummary[]? Variables { get; init; }

    public DateTimeOffset? MetadataScannedAt { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

internal sealed record ZarrVariableSummary
{
    public string Name { get; init; } = string.Empty;

    public int[] Shape { get; init; } = [];

    public int[] Chunks { get; init; } = [];

    public string DataType { get; init; } = string.Empty;

    public string? Compressor { get; init; }

    public string[] DimensionNames { get; init; } = [];
}
