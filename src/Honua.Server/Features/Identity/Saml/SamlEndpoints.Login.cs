// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Identity.Saml;

internal static partial class SamlEndpoints
{
    // SAML bindings cap RelayState at 80 bytes.
    private const int MaxRelayStateBytes = 80;

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

        var returnUrl = context.Request.Query["returnUrl"];
        if (returnUrl.Count > 1 || (returnUrl.Count == 1 && !IsLocalReturnPath(returnUrl.ToString())))
        {
            return BadRequest(context, "returnUrl must be a local path on this server.");
        }

        var request = SamlAuthnRequestBuilder.Create(opts, DateTimeOffset.UtcNow);
        correlation.Issue(context, request.Id);
        context.Response.Headers.CacheControl = "no-store";
        var location = SamlAuthnRequestBuilder.BuildRedirectUrl(
            opts.IdpSingleSignOnServiceUrl,
            SamlAuthnRequestBuilder.EncodeForRedirect(request.Xml),
            returnUrl.ToString());
        return Results.Redirect(location);
    }

    // A return URL must be a path on this origin: a single leading "/" (not "//" or "/\", which
    // browsers treat as another host), no backslashes, whitespace or control characters, and short
    // enough to travel as RelayState.
    internal static bool IsLocalReturnPath(string? value)
    {
        if (string.IsNullOrEmpty(value) || value[0] != '/' || Encoding.UTF8.GetByteCount(value) > MaxRelayStateBytes)
        {
            return false;
        }

        if (value.Length > 1 && value[1] == '/')
        {
            return false;
        }

        return !value.Any(c => c == '\\' || char.IsWhiteSpace(c) || char.IsControl(c));
    }
}
