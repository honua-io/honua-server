// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Licensing;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Identity.Saml;

/// <summary>
/// SAML 2.0 Service Provider (SP) endpoints (#508). Exposes SP metadata for IdP configuration
/// and the Assertion Consumer Service (ACS) that consumes a signed SAML assertion, maps its
/// attributes/groups to RBAC roles, and establishes a Honua admin session. Reuses the existing
/// <see cref="AdminAuthSessionStore"/> session machinery so downstream code sees the same
/// session/claims model as OIDC.
/// </summary>
internal static partial class SamlEndpoints
{
    /// <summary>Log category for SAML endpoints.</summary>
    internal sealed class SamlEndpointsLog;

    /// <summary>
    /// Maps the SAML 2.0 SP metadata and Assertion Consumer Service endpoints. Both are
    /// anonymous at the framework level (the SAML assertion is the credential) and enforce the
    /// enabled/configured guard internally.
    /// </summary>
    public static void MapSamlEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/saml")
            .WithTags("Identity", "SAML")
            .AllowAnonymous()
            // #2978: SAML SP authentication is an Enterprise entitlement (FeatureCatalog
            // .SamlAuthenticationKey; ADR-0024 Identity Governance tier). Gated at the group
            // so /metadata is covered too — an unlicensed deployment must not advertise an
            // SSO surface it cannot serve.
            .AddEndpointFilter((invocationContext, next) =>
            {
                var gate = LicenseGate.RequireEntitlement(
                    invocationContext.HttpContext,
                    FeatureCatalog.SamlAuthenticationKey,
                    "SAML 2.0 authentication");

                return gate is null
                    ? next(invocationContext)
                    : ValueTask.FromResult<object?>(gate);
            });

        group.MapGet("/metadata", HandleMetadata).WithDisplayName("SAML SP Metadata");
        group.MapPost("/acs", HandleAssertionConsumerService).WithDisplayName("SAML Assertion Consumer Service");
        group.MapPost("/slo", HandleSingleLogout).WithDisplayName("SAML Single Logout Service");
    }

    private static IResult HandleMetadata(
        HttpContext context,
        [FromServices] IOptions<SamlAuthenticationOptions> options)
    {
        var opts = options.Value;
        if (!opts.Enabled || string.IsNullOrWhiteSpace(opts.EntityId) || string.IsNullOrWhiteSpace(opts.AssertionConsumerServiceUrl))
        {
            return Results.Problem(
                title: "SAML not configured",
                detail: "The SAML SP bridge is not enabled or is missing EntityId/ACS configuration.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var metadata = BuildSpMetadata(opts);
        return Results.Content(metadata, "application/samlmetadata+xml", Encoding.UTF8);
    }

    private static async Task<IResult> HandleAssertionConsumerService(
        HttpContext context,
        [FromServices] IOptions<SamlAuthenticationOptions> options,
        [FromServices] ISamlAssertionValidator validator,
        [FromServices] AdminAuthSessionStore sessionStore,
        [FromServices] ILogger<SamlEndpointsLog> logger)
    {
        var opts = options.Value;
        if (!opts.Enabled)
        {
            return Results.Problem(
                title: "SAML not enabled",
                detail: "The SAML SP bridge is not enabled.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // codeql[cs/user-controlled-bypass]: HasFormContentType only gates whether the request
        // body is parsed as a form at all; it does not gate authentication. The session-creation
        // call below (CreateAuthenticatedSessionAsync) is reached only after the SAML assertion
        // passes SamlAssertionValidator.Validate (signature, issuer, audience, and
        // NotBefore/NotOnOrAfter checks) — a server-side cryptographic decision an attacker
        // cannot influence via this header.
        if (!context.Request.HasFormContentType)
        {
            return BadRequest(context, "Expected an HTTP-POST SAML response form.");
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
        var encodedResponse = form["SAMLResponse"].ToString();
        if (string.IsNullOrWhiteSpace(encodedResponse))
        {
            return BadRequest(context, "Missing SAMLResponse.");
        }

        string responseXml;
        try
        {
            responseXml = Encoding.UTF8.GetString(Convert.FromBase64String(encodedResponse));
        }
        catch (FormatException)
        {
            return BadRequest(context, "SAMLResponse is not valid base64.");
        }

        var result = validator.Validate(responseXml);
        if (!result.Succeeded || result.Subject is null)
        {
            // Forged, unsigned, expired, or otherwise invalid assertions land here.
            SamlLog.AssertionRejected(logger, result.FailureReason ?? "unknown");
            return Results.Problem(
                title: "SAML assertion rejected",
                detail: result.FailureReason ?? "The SAML assertion could not be validated.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var subject = result.Subject;

        // Project the validated SAML subject into the same claim shape OIDC sessions use so the
        // AdminAuthSession handler and downstream RBAC treat both identically.
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, subject.NameId),
            new(ClaimTypes.Name, string.IsNullOrWhiteSpace(subject.DisplayName) ? subject.NameId : subject.DisplayName!),
            new("auth_type", "saml"),
        };

        if (!string.IsNullOrWhiteSpace(subject.Email))
        {
            claims.Add(new Claim(ClaimTypes.Email, subject.Email!));
        }

        foreach (var role in subject.Roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        if (!AdminAuthClaimsProjector.TryProjectValidatedClaims(claims, out var sessionClaims))
        {
            return Results.Problem(
                title: "SAML assertion rejected",
                detail: "The SAML assertion produced no usable claims.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var now = DateTimeOffset.UtcNow;
        var lifetime = TimeSpan.FromSeconds(Math.Max(60, opts.SessionLifetimeSeconds));
        var expiresAt = now + lifetime;
        if (subject.SessionNotOnOrAfter is { } sessionExpiry && sessionExpiry < expiresAt)
        {
            expiresAt = sessionExpiry;
        }

        if (expiresAt <= now)
        {
            return Results.Problem(
                title: "SAML assertion rejected",
                detail: "The SAML assertion session window has already elapsed.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        // SAML has no OAuth access token; store an opaque marker so the session record is valid.
        var opaqueToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        var sessionId = await sessionStore.CreateAuthenticatedSessionAsync(
            providerKey: "saml",
            accessToken: opaqueToken,
            idToken: null,
            claims: sessionClaims,
            expiresAt: expiresAt,
            cancellationToken: context.RequestAborted).ConfigureAwait(false);

        SetSessionCookie(context, sessionId, expiresAt - now);

        SamlLog.SessionEstablished(logger, subject.NameId, subject.Roles.Count);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// Handles SAML 2.0 Single Logout (SLO) over the HTTP-POST binding. Consumes an
    /// IdP-initiated, signed <c>LogoutRequest</c>, verifies its signature against the configured
    /// IdP certificate (reusing the assertion signature path), terminates the local Honua admin
    /// session (store record + cookie), and emits a <c>LogoutResponse</c> — relayed back to the
    /// IdP's SLO endpoint via an auto-submitting form when configured, or returned directly.
    /// </summary>
    private static async Task<IResult> HandleSingleLogout(
        HttpContext context,
        [FromServices] IOptions<SamlAuthenticationOptions> options,
        [FromServices] AdminAuthSessionStore sessionStore,
        [FromServices] ILogger<SamlEndpointsLog> logger)
    {
        var opts = options.Value;
        if (!opts.Enabled)
        {
            return Results.Problem(
                title: "SAML not enabled",
                detail: "The SAML SP bridge is not enabled.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (!context.Request.HasFormContentType)
        {
            return BadRequest(context, "Expected an HTTP-POST SAML logout form.");
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
        var relayState = form["RelayState"].ToString();

        // A SAMLResponse here is the IdP acknowledging an SP-initiated logout: clear any local
        // session and finish. No signed request to validate in this direction.
        if (string.IsNullOrWhiteSpace(form["SAMLRequest"]) && !string.IsNullOrWhiteSpace(form["SAMLResponse"]))
        {
            await TerminateLocalSessionAsync(context, sessionStore).ConfigureAwait(false);
            SamlLog.SingleLogoutProcessed(logger, "(sp-initiated)");
            return TypedResults.NoContent();
        }

        var encodedRequest = form["SAMLRequest"].ToString();
        if (string.IsNullOrWhiteSpace(encodedRequest))
        {
            return BadRequest(context, "Missing SAMLRequest.");
        }

        string requestXml;
        try
        {
            requestXml = Encoding.UTF8.GetString(Convert.FromBase64String(encodedRequest));
        }
        catch (FormatException)
        {
            return BadRequest(context, "SAMLRequest is not valid base64.");
        }

        XDocument document;
        try
        {
            document = SecureXmlDocumentParser.Parse(requestXml, LoadOptions.None);
        }
        catch (XmlException)
        {
            return BadRequest(context, "Malformed SAML logout request XML.");
        }

        var logoutRequest = document.Root;
        if (logoutRequest is null || logoutRequest.Name.LocalName != "LogoutRequest")
        {
            return BadRequest(context, "Expected a samlp:LogoutRequest.");
        }

        if (string.IsNullOrWhiteSpace(opts.IdpSigningCertificate))
        {
            SamlLog.SingleLogoutRejected(logger, "no signing certificate configured");
            return LogoutRejected(context, "No IdP signing certificate configured.");
        }

        X509Certificate2 certificate;
        try
        {
            certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(opts.IdpSigningCertificate!.Trim()));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            SamlLog.SingleLogoutRejected(logger, "invalid signing certificate");
            return LogoutRejected(context, "IdP signing certificate is not valid base64 DER.");
        }

        using (certificate)
        {
            var signatureResult = SamlSignatureVerifier.Verify(logoutRequest, certificate);
            if (!signatureResult.Verified)
            {
                // Unsigned or forged LogoutRequests must not be able to terminate sessions.
                SamlLog.SingleLogoutRejected(logger, signatureResult.Reason ?? "signature verification failed");
                return LogoutRejected(context, signatureResult.Reason ?? "The SAML logout request could not be validated.");
            }
        }

        var issuer = logoutRequest.Element(XName.Get("Issuer", SamlAssertionNs))?.Value?.Trim();
        if (!string.IsNullOrEmpty(opts.IdpEntityId) &&
            !string.Equals(issuer, opts.IdpEntityId, StringComparison.Ordinal))
        {
            SamlLog.SingleLogoutRejected(logger, "issuer mismatch");
            return LogoutRejected(context, "SAML logout request issuer does not match the configured IdP.");
        }

        await TerminateLocalSessionAsync(context, sessionStore).ConfigureAwait(false);

        var nameId = logoutRequest.Element(XName.Get("NameID", SamlAssertionNs))?.Value?.Trim();
        SamlLog.SingleLogoutProcessed(logger, string.IsNullOrEmpty(nameId) ? "(unknown)" : nameId);

        var requestId = logoutRequest.Attribute("ID")?.Value;
        var logoutResponse = BuildLogoutResponse(opts, requestId);
        var encodedResponse = Convert.ToBase64String(Encoding.UTF8.GetBytes(logoutResponse));

        // Relay the LogoutResponse back to the IdP via an auto-submitting HTML form (HTTP-POST
        // binding). With no IdP SLO endpoint configured, return the response payload directly.
        if (string.IsNullOrWhiteSpace(opts.IdpSingleLogoutServiceUrl))
        {
            return Results.Content(logoutResponse, "application/samlp+xml", Encoding.UTF8);
        }

        var formHtml = BuildAutoPostForm(opts.IdpSingleLogoutServiceUrl!, encodedResponse, relayState);
        return Results.Content(formHtml, "text/html", Encoding.UTF8);
    }

    private const string SamlAssertionNs = "urn:oasis:names:tc:SAML:2.0:assertion";
    private const string SamlProtocolNs = "urn:oasis:names:tc:SAML:2.0:protocol";

    private static async Task TerminateLocalSessionAsync(HttpContext context, AdminAuthSessionStore sessionStore)
    {
        if (context.Request.Cookies.TryGetValue(AdminAuthSessionStore.AuthSessionCookieName, out var sessionId) &&
            !string.IsNullOrWhiteSpace(sessionId))
        {
            await sessionStore.RemoveAuthenticatedSessionAsync(sessionId, context.RequestAborted).ConfigureAwait(false);
        }

        context.Response.Cookies.Delete(
            AdminAuthSessionStore.AuthSessionCookieName,
            new CookieOptions
            {
                HttpOnly = true,
                IsEssential = true,
                SameSite = SameSiteMode.Lax,
                Secure = context.Request.IsHttps,
                Path = "/",
            });
    }

    private static string BuildLogoutResponse(SamlAuthenticationOptions opts, string? inResponseTo)
    {
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = false,
            OmitXmlDeclaration = false,
        };

        var output = new StringWriterUtf8();
        using (var writer = XmlWriter.Create(output, settings))
        {
            writer.WriteStartElement("samlp", "LogoutResponse", SamlProtocolNs);
            writer.WriteAttributeString("ID", "_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant());
            writer.WriteAttributeString("Version", "2.0");
            writer.WriteAttributeString("IssueInstant", DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(inResponseTo))
            {
                writer.WriteAttributeString("InResponseTo", inResponseTo);
            }

            if (!string.IsNullOrWhiteSpace(opts.EntityId))
            {
                writer.WriteStartElement("Issuer", SamlAssertionNs);
                writer.WriteString(opts.EntityId);
                writer.WriteEndElement();
            }

            writer.WriteStartElement("Status", SamlProtocolNs);
            writer.WriteStartElement("StatusCode", SamlProtocolNs);
            writer.WriteAttributeString("Value", "urn:oasis:names:tc:SAML:2.0:status:Success");
            writer.WriteEndElement(); // StatusCode
            writer.WriteEndElement(); // Status

            writer.WriteEndElement(); // LogoutResponse
        }

        return output.ToString();
    }

    private static string BuildAutoPostForm(string destination, string encodedResponse, string? relayState)
    {
        var relayInput = string.IsNullOrEmpty(relayState)
            ? string.Empty
            : $"<input type=\"hidden\" name=\"RelayState\" value=\"{WebUtility.HtmlEncode(relayState)}\"/>";

        return $"""
            <!DOCTYPE html>
            <html><head><title>SAML Single Logout</title></head>
            <body onload="document.forms[0].submit()">
            <form method="post" action="{WebUtility.HtmlEncode(destination)}">
            <input type="hidden" name="SAMLResponse" value="{WebUtility.HtmlEncode(encodedResponse)}"/>
            {relayInput}
            <noscript><button type="submit">Continue</button></noscript>
            </form>
            </body></html>
            """;
    }

    private static IResult LogoutRejected(HttpContext context, string detail)
        => Results.Problem(title: "SAML logout request rejected", detail: detail, statusCode: StatusCodes.Status401Unauthorized);

    private static string BuildSpMetadata(SamlAuthenticationOptions opts)
    {
        // Emit minimal, valid SP metadata describing the ACS endpoint and supported NameID
        // format. Written with XmlWriter so the output is well-formed and encoding-safe.
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = false,
            OmitXmlDeclaration = false,
        };

        var output = new StringWriterUtf8();
        using (var writer = XmlWriter.Create(output, settings))
        {
            const string md = "urn:oasis:names:tc:SAML:2.0:metadata";
            writer.WriteStartElement("EntityDescriptor", md);
            writer.WriteAttributeString("entityID", opts.EntityId);

            writer.WriteStartElement("SPSSODescriptor", md);
            writer.WriteAttributeString("AuthnRequestsSigned", "false");
            writer.WriteAttributeString("WantAssertionsSigned", "true");
            writer.WriteAttributeString("protocolSupportEnumeration", "urn:oasis:names:tc:SAML:2.0:protocol");

            // SingleLogoutService advertises SP-side support for SAML 2.0 Single Logout (SLO).
            // Schema order places it before NameIDFormat within an SSODescriptor.
            if (!string.IsNullOrWhiteSpace(opts.SingleLogoutServiceUrl))
            {
                writer.WriteStartElement("SingleLogoutService", md);
                writer.WriteAttributeString("Binding", "urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST");
                writer.WriteAttributeString("Location", opts.SingleLogoutServiceUrl);
                writer.WriteEndElement(); // SingleLogoutService
            }

            writer.WriteStartElement("NameIDFormat", md);
            writer.WriteString("urn:oasis:names:tc:SAML:2.0:nameid-format:persistent");
            writer.WriteEndElement();

            writer.WriteStartElement("AssertionConsumerService", md);
            writer.WriteAttributeString("Binding", "urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST");
            writer.WriteAttributeString("Location", opts.AssertionConsumerServiceUrl);
            writer.WriteAttributeString("index", "0");
            writer.WriteAttributeString("isDefault", "true");
            writer.WriteEndElement(); // AssertionConsumerService

            writer.WriteEndElement(); // SPSSODescriptor
            writer.WriteEndElement(); // EntityDescriptor
        }

        return output.ToString();
    }

    private static void SetSessionCookie(HttpContext context, string sessionId, TimeSpan lifetime)
    {
        context.Response.Cookies.Append(
            AdminAuthSessionStore.AuthSessionCookieName,
            sessionId,
            new CookieOptions
            {
                HttpOnly = true,
                IsEssential = true,
                SameSite = SameSiteMode.Lax, // Lax: the IdP POSTs cross-site to the ACS.
                Secure = context.Request.IsHttps,
                MaxAge = lifetime,
                Path = "/",
            });
    }

    private static IResult BadRequest(HttpContext context, string detail)
        => Results.Problem(title: "Invalid SAML request", detail: detail, statusCode: StatusCodes.Status400BadRequest);

    /// <summary>UTF-8 writer used so the metadata XML declaration reports utf-8 (not utf-16).</summary>
    private sealed class StringWriterUtf8 : StringWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}
