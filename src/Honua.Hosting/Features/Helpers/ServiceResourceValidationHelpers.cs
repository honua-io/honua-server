// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.Infrastructure.Helpers;

internal static class ServiceResourceValidationHelpers
{
    private const string ServiceCatalogUnavailableMessage = "Service catalog is temporarily unavailable.";

    internal readonly record struct ServiceValidationV2Result(
        bool IsValid,
        MetadataV2Service? Service,
        IResult? ErrorResult);

    internal readonly record struct ServiceLayerValidationV2Result(
        bool IsValid,
        MetadataV2Service? Service,
        MetadataV2Publication? Publication,
        MetadataV2Resource? Resource,
        IResult? ErrorResult);

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
            serviceResult = await resourceValidator.ValidateServiceV2Async(serviceId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsCatalogStorageUnavailable(ex))
        {
            return new ServiceValidationV2Result(
                false,
                null,
                StandardErrorHelpers.CreateServiceUnavailable(context, ServiceCatalogUnavailableMessage));
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
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsCatalogStorageUnavailable(ex))
        {
            return new ServiceLayerValidationV2Result(
                false,
                null,
                null,
                null,
                StandardErrorHelpers.CreateServiceUnavailable(context, ServiceCatalogUnavailableMessage));
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

    private static bool IsCatalogStorageUnavailable(Exception exception)
        => exception is DbException { SqlState: "42P01" };
}
