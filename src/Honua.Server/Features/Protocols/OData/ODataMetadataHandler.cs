// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Protocols.OData.Models;
using Honua.Server.Features.Protocols.OData.Services;

namespace Honua.Server.Features.Protocols.OData;

/// <summary>
/// Handler for OData metadata operations including service document and schema metadata.
/// Provides service discovery and metadata document generation with proper caching.
/// Reads exclusively from the Metadata v2 graph.
/// </summary>
internal sealed class ODataMetadataHandler(
    ODataMetadataService metadataService,
    ODataValidationService validationService)
{
    private readonly ODataMetadataService _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
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
        var allPublications = await ODataV2Lookups.ResolveAllODataPublicationsAsync(context, effectiveToken)
            .ConfigureAwait(false);
        if (allPublications.Length == 0)
        {
            return ODataUtilityService.CreateODataError(
                context,
                "ResourceNotFound",
                "OData is not enabled for any available service.",
                StatusCodes.Status404NotFound);
        }

        var visible = await ODataV2Lookups.ResolveVisibleODataPublicationsAsync(context, effectiveToken)
            .ConfigureAwait(false);
        if (visible.Length == 0)
        {
            return CreateAccessDeniedResult(context, allPublications);
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

        if (!XmlContentNegotiation.IsXmlAccepted(
                context.Request.Headers.Accept.ToString(),
                ["application/xml"]))
        {
            return ODataUtilityService.CreateODataError(
                context,
                "NotAcceptable",
                "Metadata requires an XML Accept header.",
                StatusCodes.Status406NotAcceptable);
        }

        ODataUtilityService.SetODataHeaders(context);
        var effectiveToken = ODataUtilityService.GetTimeoutAwareCancellationToken(context);

        var allPublications = await ODataV2Lookups.ResolveAllODataPublicationsAsync(context, effectiveToken)
            .ConfigureAwait(false);
        if (allPublications.Length == 0)
        {
            return ODataUtilityService.CreateODataError(
                context,
                "ResourceNotFound",
                "OData is not enabled for any available service.",
                StatusCodes.Status404NotFound);
        }

        var visible = await ODataV2Lookups.ResolveVisibleODataPublicationsAsync(context, effectiveToken)
            .ConfigureAwait(false);
        if (visible.Length == 0)
        {
            return CreateAccessDeniedResult(context, allPublications);
        }

        var metadata = _metadataService.GenerateMetadataDocumentV2(visible.Select(p => p.Resource));
        return TypedResults.Content(metadata, "application/xml");
    }

    private static IResult CreateAccessDeniedResult(
        HttpContext context,
        IEnumerable<ODataV2Lookups.ResolvedODataPublication> publications)
    {
        var requiresAuthentication = false;

        foreach (var pub in publications)
        {
            var decision = AccessPolicyHelpers.EvaluateAccess(
                context,
                pub.Resource.AccessPolicy,
                pub.Service.AccessPolicy);

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
