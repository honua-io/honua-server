// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Multidimensional.Domain;

namespace Honua.Server.Features.Protocols.Coverages.Multidimensional.Models;

internal sealed record RegisterMultidimensionalCoverageRequest
{
    public int LayerId { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public MultidimensionalCoverageFormat Format { get; init; }

    public CloudStorageProvider Provider { get; init; }

    public string Bucket { get; init; } = string.Empty;

    public string ObjectKey { get; init; } = string.Empty;

    public IReadOnlyList<string> Variables { get; init; } = Array.Empty<string>();
}

internal sealed record MultidimensionalCoverageRegistrationResponse
{
    public long Id { get; init; }

    public int LayerId { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public string Format { get; init; } = string.Empty;

    public string Provider { get; init; } = string.Empty;

    public string Bucket { get; init; } = string.Empty;

    public string ObjectKey { get; init; } = string.Empty;

    public IReadOnlyList<string> Variables { get; init; } = Array.Empty<string>();

    public int? Srid { get; init; }

    public int? VariableCount { get; init; }

    public DateTimeOffset? MetadataScannedAt { get; init; }

    public string? Status { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

internal sealed record MultidimensionalCoverageScanJobResponse
{
    public string JobId { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string StatusUrl { get; init; } = string.Empty;

    public string? Error { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public MultidimensionalCoverageRegistrationResponse? Coverage { get; init; }
}
