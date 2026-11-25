// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Crosswalk;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Validation;
using Honua.Protocols.OData.Models;
using Honua.Protocols.OData.Services;
using AccessDecision = Honua.Core.Features.Security.Domain.AccessDecision;

namespace Honua.Protocols.OData;

internal sealed class ODataMetadataHandler(
    ODataMetadataService metadataService,
    ODataValidationService validationService)
{
    private readonly ODataMetadataService _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
    private readonly ODataValidationService _validationService = validationService ?? throw new ArgumentNullException(nameof(validationService));

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

        var (visible, deniedResult) = await FilterAccessiblePublicationsAsync(context, allPublications, effectiveToken)
            .ConfigureAwait(false);
        if (deniedResult is not null)
        {
            return deniedResult;
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

        var (visible, deniedResult) = await FilterAccessiblePublicationsAsync(context, allPublications, effectiveToken)
            .ConfigureAwait(false);
        if (deniedResult is not null)
        {
            return deniedResult;
        }

        var snapshot = await ODataV2Lookups.GetSnapshotAsync(context, effectiveToken).ConfigureAwait(false);
        var metadata = _metadataService.GenerateMetadataDocumentV2(
            visible.Select(p => p.Resource), DescriptiveMetadataResolver.ForCatalog(snapshot.Graph));
        return TypedResults.Content(metadata, "application/xml");
    }

    private static async Task<(List<ODataV2Lookups.ResolvedODataPublication> Visible, IResult? DeniedResult)>
        FilterAccessiblePublicationsAsync(
            HttpContext context,
            ODataV2Lookups.ResolvedODataPublication[] publications,
            CancellationToken cancellationToken)
    {
        var visible = new List<ODataV2Lookups.ResolvedODataPublication>(publications.Length);
        AccessDecision? denial = null;
        foreach (var pub in publications)
        {
            var decision = await AccessPolicyHelpers.EvaluateResourceAccessAsync(
                context,
                pub.Resource,
                pub.Service,
                AuthorizationOperation.Query,
                cancellationToken).ConfigureAwait(false);
            if (decision.IsAllowed)
            {
                visible.Add(pub);
            }
            else if (denial is null || decision.RequiresAuthentication)
            {
                denial = decision;
            }
        }

        if (visible.Count == 0)
        {
            return (visible, AccessPolicyHelpers.CreateAccessDeniedResult(
                context,
                denial ?? AccessDecision.Forbidden(AccessPolicyHelpers.AccessForbiddenMessage)));
        }

        return (visible, null);
    }
}
