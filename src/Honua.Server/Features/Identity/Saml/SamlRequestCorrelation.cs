// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Identity.Saml;

// Binds a SAML response to the sign-in request this browser started. GET /saml/login stores the
// AuthnRequest ID in a short-lived, data-protected cookie; the ACS accepts a response carrying
// InResponseTo only when it names that ID, and only once. Responses without InResponseTo are
// accepted only when the unsolicited-response policy allows them.
internal sealed class SamlRequestCorrelation(
    IDataProtectionProvider protection,
    SamlAssertionReplayCache replayCache,
    IOptions<SamlAuthenticationOptions> options)
{
    internal const string CookieName = "honua_saml_request";
    internal const string UnsolicitedRejected = "Unsolicited SAML responses are not accepted.";
    internal const string NotCorrelated = "The SAML response does not match a sign-in request from this browser.";
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly ITimeLimitedDataProtector _protector = protection
        .CreateProtector("Honua.Saml.SignInRequestCorrelation.v1")
        .ToTimeLimitedDataProtector();

    private readonly SamlAuthenticationOptions _options = options.Value;

    // Unsolicited responses are allowed when explicitly enabled, or by default while the
    // deployment has no SP-initiated sign-in configured (IdP-initiated-only deployments).
    internal static bool AllowsUnsolicitedResponses(SamlAuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.AllowUnsolicitedResponses ?? string.IsNullOrWhiteSpace(options.IdpSingleSignOnServiceUrl);
    }

    public void Issue(HttpContext context, string requestId)
    {
        ArgumentNullException.ThrowIfNull(context);
        var protectedId = _protector.Protect(requestId, Lifetime);
        context.Response.Cookies.Append(CookieName, protectedId, CreateCookieOptions(context, Lifetime));
    }

    // The correlation cookie is single-use: every ACS request clears it, whatever the outcome.
    public static void Clear(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.Cookies.Delete(CookieName, CreateCookieOptions(context, maxAge: null));
    }

    // Returns null when the response may proceed, otherwise a neutral rejection reason.
    public string? Verify(HttpContext context, string? inResponseTo)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrEmpty(inResponseTo))
        {
            return AllowsUnsolicitedResponses(_options) ? null : UnsolicitedRejected;
        }

        var issuedId = ReadIssuedRequestId(context);
        if (issuedId is null || !FixedTimeEquals(issuedId, inResponseTo))
        {
            return NotCorrelated;
        }

        return replayCache.TryConsumeRequest(inResponseTo, Lifetime) ? null : NotCorrelated;
    }

    private string? ReadIssuedRequestId(HttpContext context)
    {
        if (!context.Request.Cookies.TryGetValue(CookieName, out var protectedId) || string.IsNullOrEmpty(protectedId))
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(protectedId);
        }
        // Tampered, expired, foreign-key and malformed values all mean "no issued request".
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    private static bool FixedTimeEquals(string left, string right)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

    // SameSite=None because the IdP returns the browser with a cross-site POST to the ACS; that
    // requires Secure, which is set unconditionally. The cookie is scoped to the SAML routes.
    private static CookieOptions CreateCookieOptions(HttpContext context, TimeSpan? maxAge) => new()
    {
        HttpOnly = true,
        IsEssential = true,
        Secure = true,
        SameSite = SameSiteMode.None,
        Path = context.Request.PathBase.Add("/saml").Value,
        MaxAge = maxAge,
    };
}
