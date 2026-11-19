// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Identity.Saml;

internal static partial class SamlEndpoints
{
    // SP-initiated sign-in: redirect the browser to the IdP with an AuthnRequest and remember its
    // ID in the correlation cookie so the ACS can match the response to this request.
    private static IResult HandleLogin(
        HttpContext context,
        [FromServices] IOptions<SamlAuthenticationOptions> options,
        [FromServices] SamlRequestCorrelation correlation)
    {
        var opts = options.Value;
        if (!opts.Enabled)
        {
            return Results.Problem(
                title: "SAML not enabled",
                detail: "The SAML SP bridge is not enabled.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (string.IsNullOrWhiteSpace(opts.IdpSingleSignOnServiceUrl))
        {
            return Results.Problem(
                title: "SAML sign-in not configured",
                detail: "No IdP single sign-on service URL is configured.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var request = SamlAuthnRequestBuilder.Create(opts, DateTimeOffset.UtcNow);
        correlation.Issue(context, request.Id);
        context.Response.Headers.CacheControl = "no-store";
        var location = SamlAuthnRequestBuilder.BuildRedirectUrl(
            opts.IdpSingleSignOnServiceUrl,
            SamlAuthnRequestBuilder.EncodeForRedirect(request.Xml));
        return Results.Redirect(location);
    }
}
