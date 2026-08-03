// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Raster.Functions;

/// <summary>Persists immutable, tenant-scoped versions of named raster functions.</summary>
public interface IRasterFunctionDefinitionStore
{
    /// <summary>
    /// Appends one immutable version when the caller's expected latest version still matches.
    /// Reusing an idempotency key with the same request returns the original version.
    /// </summary>
    Task<RasterFunctionDefinitionCreateResult> CreateVersionAsync(
        RasterFunctionDefinitionCreateRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets one exact version. Tenant, name, version, and normalized definition hash must all
    /// match; this method never falls back to a latest or differently hashed version.
    /// </summary>
    Task<RasterFunctionDefinitionVersion?> GetVersionAsync(
        RasterFunctionDefinitionReference reference,
        CancellationToken cancellationToken = default);
}

/// <summary>A request to append a named raster-function definition.</summary>
public sealed record RasterFunctionDefinitionCreateRequest
{
    /// <summary>Tenant that owns the name and every version below it.</summary>
    public required string TenantId { get; init; }

    /// <summary>Stable tenant-scoped function name.</summary>
    public required string Name { get; init; }

    /// <summary>Validated provider-neutral function graph.</summary>
    public required RasterFunctionDefinition Definition { get; init; }

    /// <summary>Latest version observed by the caller; zero creates the first version.</summary>
    public required int ExpectedLatestVersion { get; init; }

    /// <summary>Bounded retry key unique within this tenant-scoped name.</summary>
    public required string IdempotencyKey { get; init; }

    /// <summary>Optional caller identity retained only as audit metadata.</summary>
    public string? CreatedBy { get; init; }
}

/// <summary>An exact, fail-closed reference to one stored function version.</summary>
public sealed record RasterFunctionDefinitionReference
{
    /// <summary>Tenant that owns the definition.</summary>
    public required string TenantId { get; init; }

    /// <summary>Tenant-scoped function name.</summary>
    public required string Name { get; init; }

    /// <summary>Exact positive immutable version.</summary>
    public required int Version { get; init; }

    /// <summary>Exact lower-case SHA-256 of the normalized definition.</summary>
    public required string DefinitionHash { get; init; }
}

/// <summary>One immutable stored version of a tenant-scoped named raster function.</summary>
public sealed record RasterFunctionDefinitionVersion
{
    /// <summary>Tenant that owns the definition.</summary>
    public required string TenantId { get; init; }

    /// <summary>Tenant-scoped function name.</summary>
    public required string Name { get; init; }

    /// <summary>Monotonically increasing version.</summary>
    public required int Version { get; init; }

    /// <summary>Lower-case SHA-256 of the normalized definition.</summary>
    public required string DefinitionHash { get; init; }

    /// <summary>Immutable provider-neutral function graph.</summary>
    public required RasterFunctionDefinition Definition { get; init; }

    /// <summary>Optional audit identity supplied by the creator.</summary>
    public string? CreatedBy { get; init; }

    /// <summary>Database-assigned creation time.</summary>
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Outcome of an optimistic, idempotent function-version create.</summary>
public enum RasterFunctionDefinitionCreateStatus
{
    /// <summary>A new immutable version was appended.</summary>
    Created = 0,

    /// <summary>The same idempotent request was previously committed.</summary>
    Replayed = 1,

    /// <summary>The named function advanced beyond the caller's expected version.</summary>
    VersionConflict = 2,

    /// <summary>The idempotency key was previously used for different content or preconditions.</summary>
    IdempotencyConflict = 3,
}

/// <summary>Result of attempting to append an immutable function version.</summary>
public sealed record RasterFunctionDefinitionCreateResult
{
    /// <summary>Create outcome.</summary>
    public required RasterFunctionDefinitionCreateStatus Status { get; init; }

    /// <summary>Created or replayed version, otherwise <see langword="null"/>.</summary>
    public RasterFunctionDefinitionVersion? DefinitionVersion { get; init; }

    /// <summary>Current version observed while holding the name's serialization lock.</summary>
    public required int CurrentVersion { get; init; }
}

/// <summary>Shared admission rules for immutable raster-function definition storage.</summary>
public static class RasterFunctionDefinitionStoreValidation
{
    /// <summary>Maximum tenant identifier length, matching the tenancy contract.</summary>
    public const int MaximumTenantIdLength = 128;

    /// <summary>Maximum named-function identifier length.</summary>
    public const int MaximumNameLength = 128;

    /// <summary>Maximum idempotency-key length.</summary>
    public const int MaximumIdempotencyKeyLength = 128;

    /// <summary>Validates a create request before persistence.</summary>
    public static void Validate(RasterFunctionDefinitionCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateBoundedValue(request.TenantId, MaximumTenantIdLength, nameof(request.TenantId));
        ValidateBoundedValue(request.Name, MaximumNameLength, nameof(request.Name));
        ValidateBoundedValue(request.IdempotencyKey, MaximumIdempotencyKeyLength, nameof(request.IdempotencyKey));
        ArgumentNullException.ThrowIfNull(request.Definition);
        if (request.ExpectedLatestVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Expected latest version cannot be negative.");
        }

        if (request.CreatedBy is { Length: > 256 } || request.CreatedBy is not null && request.CreatedBy != request.CreatedBy.Trim())
        {
            throw new ArgumentException("Created-by audit metadata must be trimmed and no longer than 256 characters.", nameof(request));
        }

        var result = RasterFunctionValidator.Validate(request.Definition);
        if (!result.IsValid)
        {
            throw new ArgumentException($"Raster function definition is invalid: {result.Errors[0].Code}.", nameof(request));
        }
    }

    /// <summary>Validates an exact definition reference before lookup.</summary>
    public static void Validate(RasterFunctionDefinitionReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ValidateBoundedValue(reference.TenantId, MaximumTenantIdLength, nameof(reference.TenantId));
        ValidateBoundedValue(reference.Name, MaximumNameLength, nameof(reference.Name));
        if (reference.Version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(reference), "Definition version must be positive.");
        }

        ValidateSha256(reference.DefinitionHash, nameof(reference.DefinitionHash));
    }

    /// <summary>Validates a lower-case SHA-256 value.</summary>
    public static void ValidateSha256(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != 64 || value.Any(static character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("Hash must be a lower-case 64-character SHA-256 value.", parameterName);
        }
    }

    internal static void ValidateBoundedValue(string value, int maximumLength, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length is 0 || value.Length > maximumLength || value != value.Trim())
        {
            throw new ArgumentException("Identifier must be non-empty, trimmed, and within its length limit.", parameterName);
        }

    }
}
