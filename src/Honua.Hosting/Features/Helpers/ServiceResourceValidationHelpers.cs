// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.MultiTenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.Infrastructure.Helpers;

internal static class ServiceResourceValidationHelpers
{
    internal const string ServiceCatalogUnavailableMessage = "Service catalog is temporarily unavailable.";

    internal readonly record struct ServiceValidationV2Result(
        bool IsValid,
        MetadataV2Service? Service,
        IResult? ErrorResult);

    internal readonly record struct ServiceLayerValidationV2Result(
        bool IsValid,
        MetadataV2Service? Service,
        MetadataV2Layer? Publication,
        MetadataV2Object? Resource,
        IResult? ErrorResult);

    public static ResourceValidationResult<MetadataV2Service> ValidateResolvedServicePath(
        HttpContext context, ResourceValidationResult<MetadataV2Service> result)
        => GeoServicesServicePath.FromRequest(context) is null ||
           (result.IsValid && result.Resource is { } service && GeoServicesServicePath.TargetsService(context, service))
            ? result
            : ResourceValidationResult.NotFound<MetadataV2Service>("Service was not found.");

    public static async Task<ServiceValidationV2Result> ValidateServiceV2Async(
        IResourceValidator resourceValidator,
        string serviceId,
        string protocol,
        HttpContext context,
        Action<string>? onServiceNotFound = null,
        bool requireServiceAccess = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resourceValidator);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(protocol);
        ArgumentNullException.ThrowIfNull(context);

        ResourceValidationResult<MetadataV2Service> serviceResult;
        try
        {
            serviceResult = await resourceValidator
                .ValidateServiceV2Async(serviceId, protocol, cancellationToken)
                .ConfigureAwait(false);
            serviceResult = ValidateResolvedServicePath(context, serviceResult);
        }
        catch (Exception ex) when (IsCatalogStorageUnavailable(ex))
        {
            return new ServiceValidationV2Result(
                false,
                null,
                StandardErrorHelpers.CreateServiceUnavailable(
                    context,
                    ServiceCatalogUnavailableMessage,
                    retryable: true));
        }

        if (!serviceResult.IsValid)
        {
            var errorMessage = serviceResult.ErrorMessage ?? "Resource not found.";
            if (serviceResult.ErrorCode == ResourceValidationError.InvalidIdentifier)
            {
                return new ServiceValidationV2Result(
                    false,
                    null,
                    StandardErrorHelpers.CreateBadRequest(context, errorMessage));
            }

            onServiceNotFound?.Invoke(serviceId);
            return new ServiceValidationV2Result(
                false,
                null,
                StandardErrorHelpers.CreateNotFound(context, errorMessage));
        }

        var service = serviceResult.Resource!;
        if (!MetadataV2ServiceProtocols.IsProtocolEnabled(service, protocol))
        {
            return new ServiceValidationV2Result(
                false,
                null,
                StandardErrorHelpers.CreateNotFound(context, $"{protocol} is not enabled for this service."));
        }

        if (requireServiceAccess)
        {
            var accessError = await AccessPolicyHelpers.RequireServiceAccessAsync(
                context, service, AuthorizationOperation.Query, cancellationToken).ConfigureAwait(false);
            if (accessError != null)
            {
                return new ServiceValidationV2Result(false, null, accessError);
            }
        }

        return new ServiceValidationV2Result(true, service, null);
    }

    public static async Task<ServiceLayerValidationV2Result> ValidateServiceLayerV2Async(
        IResourceValidator resourceValidator,
        string serviceId,
        int layerId,
        string protocol,
        HttpContext context,
        Action<string>? onServiceNotFound = null,
        Action<string, int>? onLayerNotFound = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resourceValidator);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(protocol);
        ArgumentNullException.ThrowIfNull(context);

        ResourceValidationResult<MetadataV2ServiceLayerTriple> resourceResult;
        try
        {
            resourceResult = await resourceValidator.ValidateServiceLayerV2Async(
                serviceId,
                layerId,
                protocol,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsCatalogStorageUnavailable(ex))
        {
            return new ServiceLayerValidationV2Result(
                false,
                null,
                null,
                null,
                StandardErrorHelpers.CreateServiceUnavailable(
                    context,
                    ServiceCatalogUnavailableMessage,
                    retryable: true));
        }

        if (GeoServicesServicePath.FromRequest(context) is not null &&
            (!resourceResult.IsValid || !GeoServicesServicePath.TargetsService(context, resourceResult.Resource.Service)))
        {
            return new ServiceLayerValidationV2Result(false, null, null, null,
                StandardErrorHelpers.CreateNotFound(context, "Service layer was not found."));
        }

        if (!resourceResult.IsValid)
        {
            var errorMessage = resourceResult.ErrorMessage ?? "Resource not found.";
            if (resourceResult.ErrorCode == ResourceValidationError.InvalidIdentifier)
            {
                return new ServiceLayerValidationV2Result(
                    false,
                    null,
                    null,
                    null,
                    StandardErrorHelpers.CreateBadRequest(context, errorMessage));
            }

            if (errorMessage.StartsWith("Service", StringComparison.OrdinalIgnoreCase))
            {
                onServiceNotFound?.Invoke(serviceId);
            }
            else if (errorMessage.StartsWith("Layer", StringComparison.OrdinalIgnoreCase))
            {
                onLayerNotFound?.Invoke(serviceId, layerId);
            }

            return new ServiceLayerValidationV2Result(
                false,
                null,
                null,
                null,
                StandardErrorHelpers.CreateNotFound(context, errorMessage));
        }

        var triple = resourceResult.Resource;
        if (!MetadataV2ServiceProtocols.IsProtocolEnabled(triple.Service, protocol))
        {
            return new ServiceLayerValidationV2Result(
                false,
                null,
                null,
                null,
                StandardErrorHelpers.CreateNotFound(context, $"{protocol} is not enabled for this service."));
        }

        return new ServiceLayerValidationV2Result(
            true,
            triple.Service,
            triple.Publication,
            triple.Resource,
            null);
    }

    internal static bool IsCatalogStorageUnavailable(Exception exception)
        => exception is DbException { SqlState: "42P01" };

    public static async Task<(MetadataV2Object? Resource, IResult? Problem)> ValidateAdminStorageLayerAsync(
        (int StorageLayerId, HttpContext Context) request, IResourceValidator resourceValidator, CancellationToken cancellationToken)
    {
        var layerResult = await resourceValidator.ValidateStorageLayerV2Async(request.StorageLayerId, cancellationToken).ConfigureAwait(false);
        if (!layerResult.IsValid || layerResult.Resource == null)
        {
            return (null, CreateAdminValidationProblem(request.Context, layerResult, $"Layer {request.StorageLayerId} not found."));
        }

        if (!await IsAdminStorageLayerTenantVisibleAsync(request.Context, layerResult.Resource, cancellationToken).ConfigureAwait(false))
        {
            return (null, ProblemDetailsHelpers.CreateAdminProblem(
                request.Context,
                StatusCodes.Status404NotFound,
                $"Layer {request.StorageLayerId} not found."));
        }

        return (layerResult.Resource, null);
    }

    internal static async Task<bool> IsAdminStorageLayerTenantVisibleAsync(
        HttpContext context,
        MetadataV2Object resource,
        CancellationToken cancellationToken)
    {
        if (HasAdminCrossTenantAuthority(context))
        {
            return true;
        }

        var graphProvider = context.RequestServices?.GetService<IMetadataV2GraphProvider>();
        var snapshot = graphProvider is null
            ? null
            : await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        return IsAdminResourceTenantVisible(context, snapshot, resource);
    }

    internal static bool IsAdminResourceTenantVisible(
        HttpContext context,
        MetadataV2GraphSnapshot? snapshot,
        MetadataV2Object resource)
    {
        if (HasAdminCrossTenantAuthority(context))
        {
            return true;
        }

        var requestTenant = TenantScopeHelpers.ResolveRequestTenantId(context);
        if (!MetadataV2TenantVisibility.IsVisibleToTenant(resource, requestTenant))
        {
            return false;
        }

        if (snapshot is null)
        {
            return !string.IsNullOrWhiteSpace(resource.Metadata.Tenant);
        }

        return AreAdminResourcePublicationsVisible(context, snapshot, resource);
    }

    internal static bool IsAdminServiceTenantVisible(
        HttpContext context,
        MetadataV2Service service)
    {
        if (HasAdminCrossTenantAuthority(context))
        {
            return true;
        }

        var requestTenant = TenantScopeHelpers.ResolveRequestTenantId(context);
        return !string.IsNullOrWhiteSpace(service.Metadata.Tenant)
            && MetadataV2TenantVisibility.IsVisibleToTenant(service, requestTenant);
    }

    private static bool HasAdminCrossTenantAuthority(HttpContext context)
    {
        var tenantOptions = context.RequestServices?.GetService<IOptions<TenantContextOptions>>()?.Value;
        return PlatformAdminAuthorization.IsAuthorized(context.User, tenantOptions);
    }

    private static bool AreAdminResourcePublicationsVisible(
        HttpContext context,
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Object resource)
    {
        var hasTenantOwnership = !string.IsNullOrWhiteSpace(resource.Metadata.Tenant);
        var sawPublication = false;
        foreach (var publication in snapshot.Graph.Publications)
        {
            if (!string.Equals(snapshot.ResolveResource(publication)?.Metadata.Id, resource.Metadata.Id, StringComparison.Ordinal))
            {
                continue;
            }

            sawPublication = true;
            snapshot.Index.ServicesById.TryGetValue(publication.ServiceId, out var service);
            if (!TenantScopeHelpers.IsTenantVisible(context, publication, resource, service))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(publication.Metadata.Tenant)
                || !string.IsNullOrWhiteSpace(service?.Metadata.Tenant))
            {
                hasTenantOwnership = true;
            }
        }

        return sawPublication ? hasTenantOwnership : !string.IsNullOrWhiteSpace(resource.Metadata.Tenant);
    }

    public static IResult CreateAdminValidationProblem<T>(
        HttpContext context,
        ResourceValidationResult<T> validation,
        string fallbackMessage)
    {
        var statusCode = validation.ErrorCode switch
        {
            ResourceValidationError.AmbiguousIdentifier => StatusCodes.Status409Conflict,
            ResourceValidationError.InvalidIdentifier => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status404NotFound,
        };
        return ProblemDetailsHelpers.CreateAdminProblem(context, statusCode, validation.ErrorMessage ?? fallbackMessage);
    }

}
