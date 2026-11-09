// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Domain;

public sealed record CloudObjectMetadata
{
    public required long SizeBytes { get; init; }

    public string? Version { get; init; }

    public string? ETag { get; init; }

    public string? MediaType { get; init; }

    public string? ChecksumAlgorithm { get; init; }

    public string? ChecksumValue { get; init; }
}
