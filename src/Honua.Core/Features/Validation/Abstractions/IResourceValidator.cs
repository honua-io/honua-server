// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Validation.Abstractions;

/// <summary>
/// Unified resource validation service for consistent service/layer/collection existence checks
/// across all protocols (GeoServices REST, OGC API Features, OData).
/// </summary>
/// <remarks>
/// <para>
/// This interface consolidates resource validation patterns that were previously scattered
/// across protocol-specific implementations. All protocols should use this service for
/// resource existence validation to ensure consistent behavior.
/// </para>
/// <para>
/// Behavior reference: Consolidates validation patterns from:
/// - FeatureServerEditsHandler.cs (two-step service -> layer validation)
/// - FeaturesEndpoints.cs (collection ID parsing and layer lookup)
/// - ODataEndpoints.cs (implicit route parameter validation)
/// </para>
/// </remarks>
public interface IResourceValidator
{
    /// <summary>
    /// Validates that a protocol-facing layer index resolves to a canonical Metadata v2 resource.
    /// </summary>
    /// <remarks>
    /// Audit-C1: abstract (no default body) so the compiler forces every <see cref="IResourceValidator"/>
    /// implementation to ship a working override. Previously a default-interface-method that
    /// threw <see cref="NotSupportedException"/> at runtime; half-migrations were not caught at build time.
    /// </remarks>
    Task<ResourceValidationResult<MetadataV2Resource>> ValidateLayerV2Async(
        int layerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates a collection ID against canonical Metadata v2 resources.
    /// </summary>
    /// <remarks>
    /// Audit-C1: abstract (no default body) so the compiler forces every <see cref="IResourceValidator"/>
    /// implementation to ship a working override.
    /// </remarks>
    Task<ResourceValidationResult<MetadataV2Resource>> ValidateCollectionV2Async(
        string collectionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates that a service exists in the canonical Metadata v2 graph.
    /// </summary>
    /// <remarks>
    /// Audit-C1: abstract (no default body) so the compiler forces every <see cref="IResourceValidator"/>
    /// implementation to ship a working override.
    /// </remarks>
    Task<ResourceValidationResult<MetadataV2Service>> ValidateServiceV2Async(
        string serviceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates that a service and layer exist in the canonical Metadata v2 graph.
    /// Returns the resolved (service, publication, resource) triple; publications carry
    /// the protocol-facing service-local LayerIndex so callers that need it have it.
    /// </summary>
    /// <remarks>
    /// Audit-C1: abstract (no default body) so the compiler forces every <see cref="IResourceValidator"/>
    /// implementation to ship a working override.
    /// </remarks>
    Task<ResourceValidationResult<MetadataV2ServiceLayerTriple>> ValidateServiceLayerV2Async(
        string serviceId,
        int layerId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolved (service, publication, resource) triple returned by
/// <see cref="IResourceValidator.ValidateServiceLayerV2Async"/>.
/// </summary>
public readonly record struct MetadataV2ServiceLayerTriple(
    MetadataV2Service Service,
    MetadataV2Publication Publication,
    MetadataV2Resource Resource);

/// <summary>
/// Result of resource validation containing either the resource or error details.
/// </summary>
/// <typeparam name="T">Type of the validated resource.</typeparam>
public sealed class ResourceValidationResult<T>
{
    /// <summary>
    /// Whether the resource validation succeeded.
    /// </summary>
    public bool IsValid { get; }

    /// <summary>
    /// The validated resource if successful.
    /// </summary>
    public T? Resource { get; }

    /// <summary>
    /// Error code indicating the type of validation failure.
    /// </summary>
    public ResourceValidationError? ErrorCode { get; }

    /// <summary>
    /// Human-readable error message for validation failures.
    /// </summary>
    public string? ErrorMessage { get; }

    internal ResourceValidationResult(bool isValid, T? resource, ResourceValidationError? errorCode, string? errorMessage)
    {
        IsValid = isValid;
        Resource = resource;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
    }
}

/// <summary>
/// Factory helpers for resource validation results.
/// </summary>
public static class ResourceValidationResult
{
    /// <summary>
    /// Creates a successful validation result.
    /// </summary>
    public static ResourceValidationResult<T> Success<T>(T resource) => new(true, resource, null, null);

    /// <summary>
    /// Creates a validation failure for resource not found.
    /// </summary>
    public static ResourceValidationResult<T> NotFound<T>(string message) =>
        new(false, default, ResourceValidationError.NotFound, message);

    /// <summary>
    /// Creates a validation failure for invalid identifier format.
    /// </summary>
    public static ResourceValidationResult<T> InvalidIdentifier<T>(string message) =>
        new(false, default, ResourceValidationError.InvalidIdentifier, message);
}

/// <summary>
/// Error codes for resource validation failures.
/// </summary>
public enum ResourceValidationError
{
    /// <summary>
    /// The requested resource was not found.
    /// </summary>
    NotFound,

    /// <summary>
    /// The resource identifier format is invalid (e.g., non-numeric collection ID).
    /// </summary>
    InvalidIdentifier
}
