// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Identity.Saml;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Identity;

/// <summary>
/// SAML 2.0 IdP conformance matrix (#2154). Each major IdP — Okta, Microsoft Entra ID,
/// Auth0, and PingFederate — emits the role/email/display-name claims under a different
/// attribute <c>Name</c> (Entra/Auth0 use long claim-type URIs; Okta/PingFederate use short
/// directory attribute names). These tests drive the real <see cref="SamlAssertionValidator"/>
/// against an assertion shaped like each provider emits, signed by the in-box
/// <c>SignedXml</c> oracle, and assert the attribute/role mapping is correct for that
/// provider's configuration. They run in CI against mocked (locally-signed) exchanges with no
/// network dependency, so a regression in the attribute-mapping or signature path fails the
/// build. The companion docs receipt is
/// <c>docs/reference/compatibility/idp-conformance-matrix.md</c>.
/// </summary>
[Protocol(TestProtocols.Admin)]
public sealed class IdpConformanceMatrixTests
{
    private static SamlAssertionValidator CreateValidator(
        string base64Cert,
        string roleAttribute,
        string emailAttribute,
        string displayNameAttribute)
    {
        var options = new SamlAuthenticationOptions
        {
            Enabled = true,
            EntityId = SamlTestAssertions.Audience,
            IdpEntityId = SamlTestAssertions.Issuer,
            IdpSigningCertificate = base64Cert,
            RoleAttribute = roleAttribute,
            EmailAttribute = emailAttribute,
            DisplayNameAttribute = displayNameAttribute,
        };
        return new SamlAssertionValidator(Options.Create(options));
    }

    private static string Decode(string base64)
        => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));

    [UnitTest]
    public void Saml_OktaAssertion_MapsGroupsEmailAndDisplayName()
    {
        // Okta SAML apps typically emit short attribute names; group/role membership flows
        // through a "groups" attribute statement.
        const string roleAttr = "groups";
        const string emailAttr = "email";
        const string nameAttr = "displayName";

        using var cert = SamlTestAssertions.CreateSigningCertificate();
        var validator = CreateValidator(SamlTestAssertions.ToBase64Der(cert), roleAttr, emailAttr, nameAttr);
        var response = Decode(SamlTestAssertions.CreateSignedResponseWithAttributes(
            cert,
            "okta.user@example.com",
            new (string, IReadOnlyList<string>)[]
            {
                (emailAttr, new[] { "okta.user@example.com" }),
                (nameAttr, new[] { "Okta User" }),
                (roleAttr, new[] { "honua-editors", "honua-viewers" }),
            }));

        var result = validator.Validate(response);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal("okta.user@example.com", result.Subject!.NameId);
        Assert.Equal("okta.user@example.com", result.Subject.Email);
        Assert.Equal("Okta User", result.Subject.DisplayName);
        Assert.Contains("honua-editors", result.Subject.Roles);
        Assert.Contains("honua-viewers", result.Subject.Roles);
    }

    [UnitTest]
    public void Saml_EntraIdAssertion_MapsClaimUriAttributes()
    {
        // Microsoft Entra ID (Azure AD) emits long WS-* / Microsoft claim-type URIs.
        const string roleAttr = "http://schemas.microsoft.com/ws/2008/06/identity/claims/role";
        const string emailAttr = "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress";
        const string nameAttr = "http://schemas.microsoft.com/identity/claims/displayname";

        using var cert = SamlTestAssertions.CreateSigningCertificate();
        var validator = CreateValidator(SamlTestAssertions.ToBase64Der(cert), roleAttr, emailAttr, nameAttr);
        var response = Decode(SamlTestAssertions.CreateSignedResponseWithAttributes(
            cert,
            "entra.user@example.com",
            new (string, IReadOnlyList<string>)[]
            {
                (emailAttr, new[] { "entra.user@example.com" }),
                (nameAttr, new[] { "Entra User" }),
                (roleAttr, new[] { "GIS.Admin" }),
            }));

        var result = validator.Validate(response);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal("entra.user@example.com", result.Subject!.NameId);
        Assert.Equal("entra.user@example.com", result.Subject.Email);
        Assert.Equal("Entra User", result.Subject.DisplayName);
        Assert.Contains("GIS.Admin", result.Subject.Roles);
    }

    [UnitTest]
    public void Saml_Auth0Assertion_MapsNamespacedRoleClaim()
    {
        // Auth0 emits namespaced custom claims for roles alongside the WS-* email/name claims.
        const string roleAttr = "https://honua.io/saml/roles";
        const string emailAttr = "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress";
        const string nameAttr = "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name";

        using var cert = SamlTestAssertions.CreateSigningCertificate();
        var validator = CreateValidator(SamlTestAssertions.ToBase64Der(cert), roleAttr, emailAttr, nameAttr);
        var response = Decode(SamlTestAssertions.CreateSignedResponseWithAttributes(
            cert,
            "auth0|abc123",
            new (string, IReadOnlyList<string>)[]
            {
                (emailAttr, new[] { "auth0.user@example.com" }),
                (nameAttr, new[] { "Auth0 User" }),
                (roleAttr, new[] { "analyst" }),
            }));

        var result = validator.Validate(response);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal("auth0|abc123", result.Subject!.NameId);
        Assert.Equal("auth0.user@example.com", result.Subject.Email);
        Assert.Equal("Auth0 User", result.Subject.DisplayName);
        Assert.Contains("analyst", result.Subject.Roles);
    }

    [UnitTest]
    public void Saml_PingFederateAssertion_MapsDirectoryAttributeNames()
    {
        // PingFederate commonly maps straight from directory attributes (mail/cn/memberOf).
        const string roleAttr = "memberOf";
        const string emailAttr = "mail";
        const string nameAttr = "cn";

        using var cert = SamlTestAssertions.CreateSigningCertificate();
        var validator = CreateValidator(SamlTestAssertions.ToBase64Der(cert), roleAttr, emailAttr, nameAttr);
        var response = Decode(SamlTestAssertions.CreateSignedResponseWithAttributes(
            cert,
            "ping.user@example.com",
            new (string, IReadOnlyList<string>)[]
            {
                (emailAttr, new[] { "ping.user@example.com" }),
                (nameAttr, new[] { "Ping User" }),
                (roleAttr, new[] { "cn=honua-admins,ou=groups,dc=example,dc=com" }),
            }));

        var result = validator.Validate(response);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal("ping.user@example.com", result.Subject!.NameId);
        Assert.Equal("ping.user@example.com", result.Subject.Email);
        Assert.Equal("Ping User", result.Subject.DisplayName);
        Assert.Contains("cn=honua-admins,ou=groups,dc=example,dc=com", result.Subject.Roles);
    }

    [UnitTest]
    public void Saml_WhenRoleClaimMissing_FallsBackToDefaultRole()
    {
        // Conformance guard for IdPs configured without a role/group claim: the SP must still
        // establish a session at the configured default role rather than failing the login.
        const string roleAttr = "groups";
        const string emailAttr = "email";
        const string nameAttr = "displayName";

        using var cert = SamlTestAssertions.CreateSigningCertificate();
        var options = new SamlAuthenticationOptions
        {
            Enabled = true,
            EntityId = SamlTestAssertions.Audience,
            IdpEntityId = SamlTestAssertions.Issuer,
            IdpSigningCertificate = SamlTestAssertions.ToBase64Der(cert),
            RoleAttribute = roleAttr,
            EmailAttribute = emailAttr,
            DisplayNameAttribute = nameAttr,
            DefaultRole = "viewer",
        };
        var validator = new SamlAssertionValidator(Options.Create(options));
        var response = Decode(SamlTestAssertions.CreateSignedResponseWithAttributes(
            cert,
            "noroles@example.com",
            new (string, IReadOnlyList<string>)[]
            {
                (emailAttr, new[] { "noroles@example.com" }),
                (nameAttr, new[] { "No Roles" }),
            }));

        var result = validator.Validate(response);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Single(result.Subject!.Roles);
        Assert.Contains("viewer", result.Subject.Roles);
    }
}
