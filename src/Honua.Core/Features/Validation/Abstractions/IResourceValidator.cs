// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;

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
/// - FeatureServerEditHandler.cs (two-step service -> layer validation)
/// - FeaturesEndpoints.cs (collection ID parsing and layer lookup)
/// - ODataEndpoints.cs (implicit route parameter validation)
/// </para>
/// </remarks>
public interface IResourceValidator
{
    /// <summary>
    /// Validates that a layer exists and retrieves its definition.
    /// This is the primary validation method used by all protocols.
    /// </summary>
    /// <param name="layerId">The layer ID to validate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing the layer if found, or validation error details.</returns>
    Task<ResourceValidationResult<LayerDefinition>> ValidateLayerAsync(
        int layerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates a collection ID (used by OGC API Features where collection IDs are strings).
    /// Parses the collection ID and validates the corresponding layer exists.
    /// </summary>
    /// <param name="collectionId">The collection ID string to validate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing the layer if found, or validation error details.</returns>
    Task<ResourceValidationResult<LayerDefinition>> ValidateCollectionAsync(
        string collectionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates that a service exists and retrieves its definition.
    /// Used by GeoServices REST protocol which accesses layers through services.
    /// </summary>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing the service if found, or validation error details.</returns>
    Task<ResourceValidationResult<ServiceDefinition>> ValidateServiceAsync(
        string serviceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates that a service and layer exist, and retrieves both definitions.
    /// Used by GeoServices REST protocol for two-level resource hierarchy.
    /// </summary>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="layerId">The layer ID within the service.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing both service and layer if found, or validation error details.</returns>
    Task<ResourceValidationResult<(ServiceDefinition Service, LayerDefinition Layer)>> ValidateServiceLayerAsync(
        string serviceId,
        int layerId,
        CancellationToken cancellationToken = default);
}

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
