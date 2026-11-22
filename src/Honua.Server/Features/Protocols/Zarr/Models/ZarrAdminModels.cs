// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Domain;

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
        if (CloudRasterRegistrationValidation.ValidateMetadata(LayerId, Name, Description) is { } metadataError)
        {
            error = metadataError;
            return false;
        }
        if (CloudRasterRegistrationValidation.ValidateStorageLocation(Bucket, RootPath, "rootPath") is { } storageError)
        {
            error = storageError;
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
