// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Honua.Infrastructure.Authentication;
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
            .AllowAnonymous();

        group.MapGet("/metadata", HandleMetadata).WithDisplayName("SAML SP Metadata");
        group.MapPost("/acs", HandleAssertionConsumerService).WithDisplayName("SAML Assertion Consumer Service");
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
