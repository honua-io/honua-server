// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Raster;

/// <summary>
/// Resolves raster source metadata without downloading raster content. Implementations must ignore
/// the caller-authored security-context hint for authorization and credential acquisition. Access
/// comes only from the authenticated server execution context; #3068 will define minting and
/// revalidation of a worker-owned context.
/// </summary>
public interface IRasterSourceMetadataResolver
{
    /// <summary>Resolves pinned identity metadata without reading the raster payload.</summary>
    /// <param name="descriptor">Validated provider-neutral source descriptor whose security-context reference remains untrusted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Availability and immutable metadata, or a caller-safe failure status.</returns>
    Task<RasterSourceMetadataResolution> ResolveMetadataAsync(
        RasterSourceDescriptor descriptor,
        CancellationToken cancellationToken = default);
}

/// <summary>Metadata resolved for one raster source without materializing its content.</summary>
public sealed record RasterSourceMetadata
{
    /// <summary>Resolved immutable source version.</summary>
    public required string Version { get; init; }

    /// <summary>Resolved encoded content identity.</summary>
    public required RasterContentIdentity Content { get; init; }
}

/// <summary>Metadata-only source resolution status.</summary>
public enum RasterSourceMetadataStatus
{
    /// <summary>The pinned source exists and its metadata matches.</summary>
    Available,

    /// <summary>The source no longer exists.</summary>
    Missing,

    /// <summary>The source exists but no longer has the requested immutable version.</summary>
    Stale,

    /// <summary>The resolved size or checksum does not match the descriptor.</summary>
    IntegrityMismatch,

    /// <summary>The descriptor did not pass admission validation.</summary>
    Invalid,

    /// <summary>The execution identity is not authorized to inspect the source.</summary>
    Denied,
}

/// <summary>Outcome of resolving raster source metadata.</summary>
public sealed record RasterSourceMetadataResolution
{
    /// <summary>Resolution status.</summary>
    public required RasterSourceMetadataStatus Status { get; init; }

    /// <summary>Resolved metadata when <see cref="Status"/> is <see cref="RasterSourceMetadataStatus.Available"/>.</summary>
    public RasterSourceMetadata? Metadata { get; init; }

    /// <summary>Stable caller-safe failure code, without provider paths or credentials.</summary>
    public string? FailureCode { get; init; }

    /// <summary>Creates an available result.</summary>
    /// <param name="metadata">Resolved immutable metadata.</param>
    /// <returns>An available result.</returns>
    public static RasterSourceMetadataResolution Available(RasterSourceMetadata metadata) =>
        new() { Status = RasterSourceMetadataStatus.Available, Metadata = metadata };

    /// <summary>Creates a non-available result.</summary>
    /// <param name="status">Non-available status.</param>
    /// <param name="failureCode">Stable caller-safe failure code.</param>
    /// <returns>A failed result.</returns>
    public static RasterSourceMetadataResolution Failure(
        RasterSourceMetadataStatus status,
        string failureCode)
    {
        if (status == RasterSourceMetadataStatus.Available)
        {
            throw new ArgumentOutOfRangeException(nameof(status), "Use Available for successful resolution.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        return new RasterSourceMetadataResolution { Status = status, FailureCode = failureCode };
    }
}

/// <summary>
/// Validates and verifies metadata-only raster source resolution before durable execution.
/// </summary>
public static class RasterSourceMetadataAdmission
{
    /// <summary>Validates a descriptor, resolves only its metadata, and verifies its immutable pin.</summary>
    /// <param name="descriptor">Source descriptor.</param>
    /// <param name="resolver">Provider-neutral metadata resolver.</param>
    /// <param name="options">Optional validation limits.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A verified availability or safe failure result.</returns>
    public static async Task<RasterSourceMetadataResolution> ResolveAsync(
        RasterSourceDescriptor descriptor,
        IRasterSourceMetadataResolver resolver,
        RasterSourceValidationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(resolver);
        cancellationToken.ThrowIfCancellationRequested();

        var validation = RasterSourceDescriptorValidator.Validate(descriptor, options, cancellationToken);
        if (!validation.IsValid)
        {
            return RasterSourceMetadataResolution.Failure(
                RasterSourceMetadataStatus.Invalid,
                validation.Errors[0].Code);
        }

        var resolution = await resolver.ResolveMetadataAsync(descriptor, cancellationToken).ConfigureAwait(false);
        if (resolution.Status != RasterSourceMetadataStatus.Available)
        {
            return resolution;
        }

        var metadata = resolution.Metadata;
        if (metadata?.Content is null)
        {
            return RasterSourceMetadataResolution.Failure(
                RasterSourceMetadataStatus.Invalid,
                "metadata_missing");
        }

        if (!string.IsNullOrWhiteSpace(descriptor.Version)
            && !string.Equals(descriptor.Version, metadata.Version, StringComparison.Ordinal))
        {
            return RasterSourceMetadataResolution.Failure(
                RasterSourceMetadataStatus.Stale,
                "source_version_changed");
        }

        var descriptorContent = descriptor.Content;
        if (descriptorContent.ETag is { } expectedETag
            && !string.Equals(expectedETag, metadata.Content.ETag, StringComparison.Ordinal))
        {
            return RasterSourceMetadataResolution.Failure(
                RasterSourceMetadataStatus.Stale,
                "source_etag_changed");
        }

        if (descriptorContent.SizeBytes != metadata.Content.SizeBytes
            || !ChecksumMatches(descriptorContent.Checksum, metadata.Content.Checksum))
        {
            return RasterSourceMetadataResolution.Failure(
                RasterSourceMetadataStatus.IntegrityMismatch,
                "source_integrity_mismatch");
        }

        return resolution;
    }

    private static bool ChecksumMatches(RasterChecksum? expected, RasterChecksum? actual) =>
        expected is null
        || (actual is not null
            && string.Equals(expected.Algorithm, actual.Algorithm, StringComparison.Ordinal)
            && string.Equals(expected.Value, actual.Value, StringComparison.OrdinalIgnoreCase));
}
