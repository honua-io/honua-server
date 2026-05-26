// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
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

    /// <summary>
    /// V2 equivalent of <see cref="ServiceValidationResult"/> carrying the resolved
    /// <see cref="MetadataV2Service"/>.
    /// </summary>
    internal readonly record struct ServiceValidationV2Result(
        bool IsValid,
        MetadataV2Service? Service,
        IResult? ErrorResult);

    /// <summary>
    /// V2 equivalent of <see cref="ServiceLayerValidationResult"/> carrying the resolved
    /// (service, publication, resource) triple.
    /// </summary>
    internal readonly record struct ServiceLayerValidationV2Result(
        bool IsValid,
        MetadataV2Service? Service,
        MetadataV2Publication? Publication,
        MetadataV2Resource? Resource,
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

    /// <summary>
    /// V2 overload of <see cref="ValidateServiceAsync(IResourceValidator, string, string, HttpContext, Action{string}?, CancellationToken)"/>
    /// that resolves the canonical <see cref="MetadataV2Service"/> from the V2 graph and
    /// applies the protocol-enabled check via <see cref="ServiceProtocols.IsProtocolEnabled(MetadataV2Service?, string)"/>.
    /// </summary>
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
        catch (PostgresException ex) when (IsCatalogStorageUnavailable(ex))
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
        if (!ServiceProtocols.IsProtocolEnabled(service, protocol))
        {
            return new ServiceValidationV2Result(
                false,
                null,
                StandardErrorHelpers.CreateNotFound(context, $"{protocol} is not enabled for this service."));
        }

        if (requireServiceAccess)
        {
            var accessError = AccessPolicyHelpers.RequireServiceAccess(context, service);
            if (accessError != null)
            {
                return new ServiceValidationV2Result(false, null, accessError);
            }
        }

        return new ServiceValidationV2Result(true, service, null);
    }

    /// <summary>
    /// V2 overload of <see cref="ValidateServiceLayerAsync(IResourceValidator, string, int, string, HttpContext, Action{string}?, Action{string, int}?, CancellationToken)"/>
    /// that resolves the (service, publication, resource) triple via the V2 graph.
    /// </summary>
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
        catch (PostgresException ex) when (IsCatalogStorageUnavailable(ex))
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
        if (!ServiceProtocols.IsProtocolEnabled(triple.Service, protocol))
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

    private static bool IsCatalogStorageUnavailable(PostgresException exception)
        => exception.SqlState == PostgresErrorCodes.UndefinedTable;
}
