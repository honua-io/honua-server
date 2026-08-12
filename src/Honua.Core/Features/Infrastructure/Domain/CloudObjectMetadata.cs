// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Domain;

/// <summary>
/// Bounded identity metadata returned by an object-store HEAD/properties request.
/// No object payload is read while producing this value.
/// </summary>
public sealed record CloudObjectMetadata
{
    /// <summary>Encoded object size in bytes.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>Provider-native immutable version identifier, when versioning is enabled.</summary>
    public string? Version { get; init; }

    /// <summary>Provider ETag for conditional reads and stale-object detection.</summary>
    public string? ETag { get; init; }

    /// <summary>Object content type, when supplied by the provider.</summary>
    public string? MediaType { get; init; }

    /// <summary>Lower-case strong-checksum algorithm, when supplied by the provider.</summary>
    public string? ChecksumAlgorithm { get; init; }

    /// <summary>Provider checksum value, normalized to hexadecimal when supplied.</summary>
    public string? ChecksumValue { get; init; }
}
