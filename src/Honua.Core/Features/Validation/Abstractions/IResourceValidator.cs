// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Validation.Abstractions;

public interface IResourceValidator
{
    Task<ResourceValidationResult<MetadataV2Resource>> ValidateStorageLayerV2Async(
        int storageLayerId,
        CancellationToken cancellationToken = default);

    Task<ResourceValidationResult<MetadataV2Resource>> ValidateLayerV2Async(
        int layerId,
        CancellationToken cancellationToken = default);

    Task<ResourceValidationResult<MetadataV2Resource>> ValidateCollectionV2Async(
        string collectionId,
        CancellationToken cancellationToken = default);

    Task<ResourceValidationResult<MetadataV2Service>> ValidateServiceV2Async(
        string serviceId,
        CancellationToken cancellationToken = default);

    Task<ResourceValidationResult<MetadataV2Service>> ValidateServiceV2Async(
        string serviceId,
        string requiredProtocol,
        CancellationToken cancellationToken = default);

    Task<ResourceValidationResult<MetadataV2ServiceLayerTriple>> ValidateServiceLayerV2Async(
        string serviceId,
        int layerId,
        CancellationToken cancellationToken = default);

    Task<ResourceValidationResult<MetadataV2ServiceLayerTriple>> ValidateServiceLayerV2Async(
        string serviceId,
        int layerId,
        string requiredProtocol,
        CancellationToken cancellationToken = default);
}

public readonly record struct MetadataV2ServiceLayerTriple(
    MetadataV2Service Service,
    MetadataV2Publication Publication,
    MetadataV2Resource Resource)
{
    public int? StorageLayerId { get; init; }

    // Provider routing must use this snapshot so a concurrent publication rebind cannot change the route.
    public MetadataV2GraphSnapshot? Snapshot { get; init; }
}

public sealed class ResourceValidationResult<T>
{
    public bool IsValid { get; }

    public T? Resource { get; }

    public ResourceValidationError? ErrorCode { get; }

    public string? ErrorMessage { get; }

    internal ResourceValidationResult(bool isValid, T? resource, ResourceValidationError? errorCode, string? errorMessage)
    {
        IsValid = isValid;
        Resource = resource;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
    }
}

public static class ResourceValidationResult
{
    public static ResourceValidationResult<T> Success<T>(T resource) => new(true, resource, null, null);

    public static ResourceValidationResult<T> NotFound<T>(string message) =>
        new(false, default, ResourceValidationError.NotFound, message);

    public static ResourceValidationResult<T> InvalidIdentifier<T>(string message) =>
        new(false, default, ResourceValidationError.InvalidIdentifier, message);
}

public enum ResourceValidationError
{
    NotFound,

    InvalidIdentifier
}
