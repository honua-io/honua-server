// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Multidimensional.Domain;

namespace Honua.Server.Features.Protocols.Coverages.Multidimensional.Models;

/// <summary>
/// Admin request payload to register a cloud-hosted multidimensional coverage
/// source (cloud-optimized HDF5 or NetCDF4).
/// </summary>
internal sealed record RegisterMultidimensionalCoverageRequest
{
    /// <summary>Layer to associate the coverage with.</summary>
    public int LayerId { get; init; }

    /// <summary>Human-readable name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Optional description.</summary>
    public string? Description { get; init; }

    /// <summary>Container format of the registered object.</summary>
    public MultidimensionalCoverageFormat Format { get; init; }

    /// <summary>Cloud storage provider.</summary>
    public CloudStorageProvider Provider { get; init; }

    /// <summary>Bucket or container name.</summary>
    public string Bucket { get; init; } = string.Empty;

    /// <summary>Object key or blob path.</summary>
    public string ObjectKey { get; init; } = string.Empty;

    /// <summary>
    /// Operator-declared variable names. Empty array means "expose every CF
    /// data variable discovered during the next metadata scan".
    /// </summary>
    public IReadOnlyList<string> Variables { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Admin response payload for a multidimensional coverage registration.
/// </summary>
internal sealed record MultidimensionalCoverageRegistrationResponse
{
    /// <summary>Registration identifier.</summary>
    public long Id { get; init; }

    /// <summary>Associated layer.</summary>
    public int LayerId { get; init; }

    /// <summary>Registration name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Optional description.</summary>
    public string? Description { get; init; }

    /// <summary>Container format.</summary>
    public string Format { get; init; } = string.Empty;

    /// <summary>Cloud storage provider.</summary>
    public string Provider { get; init; } = string.Empty;

    /// <summary>Bucket or container name.</summary>
    public string Bucket { get; init; } = string.Empty;

    /// <summary>Object key or blob path.</summary>
    public string ObjectKey { get; init; } = string.Empty;

    /// <summary>Operator-declared variable names.</summary>
    public IReadOnlyList<string> Variables { get; init; } = Array.Empty<string>();

    /// <summary>SRID derived from CF grid_mapping (null until metadata is scanned).</summary>
    public int? Srid { get; init; }

    /// <summary>Discovered data variable count (null until metadata is scanned).</summary>
    public int? VariableCount { get; init; }

    /// <summary>When metadata was last scanned.</summary>
    public DateTimeOffset? MetadataScannedAt { get; init; }

    /// <summary>
    /// Set when this registration is in a known non-readable state, with a
    /// stable problem code. Null in the happy path.
    /// </summary>
    public string? Status { get; init; }

    /// <summary>Registration timestamp.</summary>
    public DateTimeOffset CreatedAt { get; init; }
}
