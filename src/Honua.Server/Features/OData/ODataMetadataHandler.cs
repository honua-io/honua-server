// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
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
        var accessError = AccessPolicyHelpers.RequireAnyLayerAccess(context, layers);
        if (accessError != null)
        {
            return accessError;
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

        ODataUtilityService.SetODataHeaders(context);
        var effectiveToken = ODataUtilityService.GetTimeoutAwareCancellationToken(context);
        var layers = await _layerCatalog.ListLayersAsync(effectiveToken);
        var visibleLayers = layers
            .Where(layer => AccessPolicyHelpers.IsLayerAccessible(context, layer))
            .ToArray();
        if (visibleLayers.Length == 0)
        {
            var accessError = AccessPolicyHelpers.RequireAnyLayerAccess(context, layers);
            if (accessError != null)
            {
                return accessError;
            }
        }

        var metadata = await _metadataService.GenerateMetadataDocumentAsync(visibleLayers, effectiveToken);
        return TypedResults.Content(metadata, "application/xml");
    }

}
