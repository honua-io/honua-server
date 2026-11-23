// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Honua.Server.Features.Identity.Saml;

internal static partial class SamlEndpoints
{
    // The HTTP-POST relay page posts the LogoutResponse to the IdP, another origin, from a
    // script. The site-wide policy (script-src 'self', form-action 'self') would block both,
    // so this one response carries its own policy: only its nonce'd script runs and its form
    // may post only to the IdP's origin.
    private sealed record LogoutRelay(string Destination, string EncodedResponse, string? RelayState);

    private static IResult LogoutRelayPage(HttpContext context, LogoutRelay relay)
    {
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var headers = context.Response.Headers;
        headers.Remove("Content-Security-Policy-Report-Only");
        headers["Content-Security-Policy"] = RelayPagePolicy(relay.Destination, nonce);
        headers.CacheControl = "no-store";
        return Results.Content(BuildAutoPostForm(relay, nonce), "text/html", Encoding.UTF8);
    }

    private static string RelayPagePolicy(string destination, string nonce)
    {
        var formTarget = Uri.TryCreate(destination, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority)
            : "'none'";
        return $"default-src 'none'; script-src 'nonce-{nonce}'; form-action {formTarget}; " +
               "frame-ancestors 'none'; base-uri 'none'";
    }

    private static string BuildAutoPostForm(LogoutRelay relay, string nonce)
    {
        var relayInput = string.IsNullOrEmpty(relay.RelayState)
            ? string.Empty
            : $"<input type=\"hidden\" name=\"RelayState\" value=\"{WebUtility.HtmlEncode(relay.RelayState)}\"/>";

        return $"""
            <!DOCTYPE html>
            <html><head><title>SAML Single Logout</title></head>
            <body>
            <form method="post" action="{WebUtility.HtmlEncode(relay.Destination)}">
            <input type="hidden" name="SAMLResponse" value="{WebUtility.HtmlEncode(relay.EncodedResponse)}"/>
            {relayInput}
            <button type="submit">Continue</button>
            </form>
            <script nonce="{nonce}">document.forms[0].submit();</script>
            </body></html>
            """;
    }
}
