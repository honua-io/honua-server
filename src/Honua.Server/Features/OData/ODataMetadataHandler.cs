// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.OData.Models;
using Honua.Server.Features.OData.Services;

namespace Honua.Server.Features.OData;

/// <summary>
/// Handler for OData metadata operations including service document and schema metadata.
/// Provides service discovery and metadata document generation with proper caching.
/// </summary>
internal sealed class ODataMetadataHandler(
    ODataMetadataService metadataService,
    ILayerCatalog layerCatalog,
    ODataValidationService validationService)
{
    private readonly ODataMetadataService _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
    private readonly ILayerCatalog _layerCatalog = layerCatalog ?? throw new ArgumentNullException(nameof(layerCatalog));
    private readonly ODataValidationService _validationService = validationService ?? throw new ArgumentNullException(nameof(validationService));

    /// <summary>
    /// Handles OData service document request
    /// </summary>
    public async Task<IResult> HandleGetServiceDocument(HttpContext context)
    {
        var queryValidation = ODataRequestValidation.ValidateAllowedParameters(
            context,
            _validationService,
            AllowedQueryParameters.None);
        if (queryValidation != null)
        {
            return queryValidation;
        }

        var effectiveToken = ODataUtilityService.GetTimeoutAwareCancellationToken(context);
        var layers = await _layerCatalog.ListLayersAsync(effectiveToken);
        var services = await _layerCatalog.ListServicesAsync(effectiveToken);
        var primaryServices = LayerValidationHelpers.BuildPrimaryServiceMap(services, ServiceProtocols.OData);
        var protocolLayers = layers
            .Where(layer => IsODataLayerEnabled(layer, primaryServices))
            .ToArray();
        if (protocolLayers.Length == 0)
        {
            return ODataUtilityService.CreateODataError(
                context,
                "ResourceNotFound",
                "OData is not enabled for any available service.",
                StatusCodes.Status404NotFound);
        }

        if (!HasAccessibleLayer(context, protocolLayers, primaryServices))
        {
            return CreateAccessDeniedResult(context, protocolLayers, primaryServices);
        }

        var baseUrl = ODataUtilityService.GetBaseUrl(context.Request);
        var generatedDocument = _metadataService.GenerateServiceDocument(baseUrl);
        var includeContext = ODataUtilityService.ShouldIncludeContext(context.Request, format: null);
        var serviceDocument = new ServiceDocument
        {
            Context = includeContext ? generatedDocument.Context : null,
            Value = generatedDocument.Value
        };

        ODataUtilityService.SetODataHeaders(context);
        return Results.Json(serviceDocument, ODataJsonContext.Default.ServiceDocument,
            contentType: ODataUtilityService.GetODataContentType(context.Request, format: null));
    }

    /// <summary>
    /// Handles OData metadata document request
    /// </summary>
    public async Task<IResult> HandleGetMetadataAsync(
        HttpContext context,
        CancellationToken cancellationToken = default)
    {
        var queryValidation = ODataRequestValidation.ValidateAllowedParameters(
            context,
            _validationService,
            AllowedQueryParameters.None);
        if (queryValidation != null)
        {
            return queryValidation;
        }

        if (!XmlContentNegotiation.IsXmlAccepted(context.Request.Headers.Accept.ToString()))
        {
            return Results.StatusCode(StatusCodes.Status406NotAcceptable);
        }

        ODataUtilityService.SetODataHeaders(context);
        var effectiveToken = ODataUtilityService.GetTimeoutAwareCancellationToken(context);
        var layers = await _layerCatalog.ListLayersAsync(effectiveToken);
        var services = await _layerCatalog.ListServicesAsync(effectiveToken);
        var primaryServices = LayerValidationHelpers.BuildPrimaryServiceMap(services, ServiceProtocols.OData);
        var protocolLayers = layers
            .Where(layer => IsODataLayerEnabled(layer, primaryServices))
            .ToArray();
        if (protocolLayers.Length == 0)
        {
            return ODataUtilityService.CreateODataError(
                context,
                "ResourceNotFound",
                "OData is not enabled for any available service.",
                StatusCodes.Status404NotFound);
        }

        var visibleLayers = protocolLayers
            .Where(layer => IsODataLayerVisible(context, layer, primaryServices))
            .ToArray();
        if (visibleLayers.Length == 0)
        {
            return CreateAccessDeniedResult(context, protocolLayers, primaryServices);
        }

        var metadata = await _metadataService.GenerateMetadataDocumentAsync(visibleLayers, effectiveToken);
        return TypedResults.Content(metadata, "application/xml");
    }

    private static bool IsODataLayerEnabled(
        LayerDefinition layer,
        IReadOnlyDictionary<int, ServiceDefinition> primaryServices)
    {
        if (primaryServices.TryGetValue(layer.Id, out var service))
        {
            return ServiceProtocols.IsProtocolEnabled(service.Metadata, ServiceProtocols.OData);
        }

        return ServiceProtocols.IsProtocolEnabled(layer.Metadata, ServiceProtocols.OData);
    }

    private static bool IsODataLayerVisible(
        HttpContext context,
        LayerDefinition layer,
        IReadOnlyDictionary<int, ServiceDefinition> primaryServices)
    {
        if (primaryServices.TryGetValue(layer.Id, out var service))
        {
            return AccessPolicyHelpers.IsLayerAccessible(context, layer, service);
        }

        return AccessPolicyHelpers.IsLayerAccessible(context, layer);
    }

    private static bool HasAccessibleLayer(
        HttpContext context,
        IEnumerable<LayerDefinition> layers,
        IReadOnlyDictionary<int, ServiceDefinition> primaryServices)
        => layers.Any(layer => IsODataLayerVisible(context, layer, primaryServices));

    private static IResult CreateAccessDeniedResult(
        HttpContext context,
        IEnumerable<LayerDefinition> layers,
        IReadOnlyDictionary<int, ServiceDefinition> primaryServices)
    {
        var requiresAuthentication = false;

        foreach (var layer in layers)
        {
            primaryServices.TryGetValue(layer.Id, out var service);
            var decision = AccessPolicyHelpers.EvaluateAccess(
                context,
                layer.Metadata?.AccessPolicy,
                service?.Metadata?.AccessPolicy);

            if (decision.RequiresAuthentication)
            {
                requiresAuthentication = true;
            }
        }

        return requiresAuthentication
            ? StandardErrorHelpers.CreateUnauthorized(context, AccessPolicyHelpers.AuthRequiredMessage)
            : StandardErrorHelpers.CreateForbidden(context, AccessPolicyHelpers.AccessForbiddenMessage);
    }
}
