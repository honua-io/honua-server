// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Npgsql;

namespace Honua.Server.Features.Infrastructure.Helpers;

internal static class ServiceResourceValidationHelpers
{
    private const string ServiceCatalogUnavailableMessage = "Service catalog is temporarily unavailable.";

    internal readonly record struct ServiceValidationResult(
        bool IsValid,
        ServiceDefinition? Service,
        IResult? ErrorResult);

    internal readonly record struct ServiceLayerValidationResult(
        bool IsValid,
        ServiceDefinition? Service,
        LayerDefinition? Layer,
        IResult? ErrorResult);

    public static async Task<ServiceValidationResult> ValidateServiceAsync(
        IResourceValidator resourceValidator,
        string serviceId,
        string protocol,
        HttpContext context,
        Action<string>? onServiceNotFound = null,
        CancellationToken cancellationToken = default)
        => await ValidateServiceAsync(
            resourceValidator,
            serviceId,
            protocol,
            context,
            onServiceNotFound,
            requireServiceAccess: false,
            cancellationToken)
            .ConfigureAwait(false);

    public static async Task<ServiceValidationResult> ValidateServiceAsync(
        IResourceValidator resourceValidator,
        string serviceId,
        string protocol,
        HttpContext context,
        Action<string>? onServiceNotFound,
        bool requireServiceAccess,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resourceValidator);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(protocol);
        ArgumentNullException.ThrowIfNull(context);

        ResourceValidationResult<ServiceDefinition> serviceResult;
        try
        {
            serviceResult = await resourceValidator.ValidateServiceAsync(serviceId, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (IsCatalogStorageUnavailable(ex))
        {
            return new ServiceValidationResult(
                false,
                null,
                StandardErrorHelpers.CreateServiceUnavailable(context, ServiceCatalogUnavailableMessage));
        }

        if (!serviceResult.IsValid)
        {
            var errorMessage = serviceResult.ErrorMessage ?? "Resource not found.";
            if (serviceResult.ErrorCode == ResourceValidationError.InvalidIdentifier)
            {
                return new ServiceValidationResult(
                    false,
                    null,
                    StandardErrorHelpers.CreateBadRequest(context, errorMessage));
            }

            onServiceNotFound?.Invoke(serviceId);
            return new ServiceValidationResult(
                false,
                null,
                StandardErrorHelpers.CreateNotFound(context, errorMessage));
        }

        var service = serviceResult.Resource!;
        var protocolError = ProtocolValidationHelpers.ValidateProtocolEnabled(context, service, protocol);
        if (protocolError != null)
        {
            return new ServiceValidationResult(false, null, protocolError);
        }

        if (requireServiceAccess)
        {
            var accessError = AccessPolicyHelpers.RequireServiceAccess(context, service);
            if (accessError != null)
            {
                return new ServiceValidationResult(false, null, accessError);
            }
        }

        return new ServiceValidationResult(true, service, null);
    }

    public static async Task<ServiceLayerValidationResult> ValidateServiceLayerAsync(
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

        ResourceValidationResult<(ServiceDefinition Service, LayerDefinition Layer)> resourceResult;
        try
        {
            resourceResult = await resourceValidator.ValidateServiceLayerAsync(
                serviceId,
                layerId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (IsCatalogStorageUnavailable(ex))
        {
            return new ServiceLayerValidationResult(
                false,
                null,
                null,
                StandardErrorHelpers.CreateServiceUnavailable(context, ServiceCatalogUnavailableMessage));
        }

        if (!resourceResult.IsValid)
        {
            var errorMessage = resourceResult.ErrorMessage ?? "Resource not found.";
            if (resourceResult.ErrorCode == ResourceValidationError.InvalidIdentifier)
            {
                return new ServiceLayerValidationResult(
                    false,
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

            return new ServiceLayerValidationResult(
                false,
                null,
                null,
                StandardErrorHelpers.CreateNotFound(context, errorMessage));
        }

        var service = resourceResult.Resource!.Service;
        var protocolError = ProtocolValidationHelpers.ValidateProtocolEnabled(
            context,
            service,
            protocol);
        if (protocolError != null)
        {
            return new ServiceLayerValidationResult(false, null, null, protocolError);
        }

        return new ServiceLayerValidationResult(
            true,
            service,
            resourceResult.Resource.Layer,
            null);
    }

    private static bool IsCatalogStorageUnavailable(PostgresException exception)
        => exception.SqlState == PostgresErrorCodes.UndefinedTable;
}
