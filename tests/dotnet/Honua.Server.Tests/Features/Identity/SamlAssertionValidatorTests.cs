// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Server.Features.Identity.Saml;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Identity;

/// <summary>
/// Unit tests for <see cref="SamlAssertionValidator"/> (#508): a valid signed assertion is
/// accepted and its attributes mapped, while forged/unsigned/expired/wrong-audience assertions
/// are rejected. The signed fixtures are produced by the in-box <c>SignedXml</c> reference
/// implementation, so these tests also prove the AOT-safe Exclusive-C14N verifier interoperates
/// with a standard signer.
/// </summary>
[Protocol(TestProtocols.Admin)]
public sealed class SamlAssertionValidatorTests
{
    private static SamlAssertionValidator CreateValidator(string base64Cert, Action<SamlAuthenticationOptions>? configure = null)
    {
        var options = new SamlAuthenticationOptions
        {
            Enabled = true,
            EntityId = SamlTestAssertions.Audience,
            IdpEntityId = SamlTestAssertions.Issuer,
            IdpSigningCertificate = base64Cert,
            RoleAttribute = "Role",
            EmailAttribute = "email",
            DisplayNameAttribute = "displayName",
        };
        configure?.Invoke(options);
        return new SamlAssertionValidator(Options.Create(options));
    }

    private static string Decode(string base64) => Encoding.UTF8.GetString(Convert.FromBase64String(base64));

    [UnitTest]
    public void Validate_ValidSignedAssertion_SucceedsAndMapsAttributes()
    {
        using var cert = SamlTestAssertions.CreateSigningCertificate();
        var validator = CreateValidator(SamlTestAssertions.ToBase64Der(cert));
        var response = Decode(SamlTestAssertions.CreateSignedResponse(
            cert, "user@example.com", "user@example.com", "Display Name", ["editor", "viewer"]));

        var result = validator.Validate(response);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.NotNull(result.Subject);
        Assert.Equal("user@example.com", result.Subject!.NameId);
        Assert.Equal("Display Name", result.Subject.DisplayName);
        Assert.Equal("user@example.com", result.Subject.Email);
        Assert.Contains("editor", result.Subject.Roles);
        Assert.Contains("viewer", result.Subject.Roles);
    }

    [UnitTest]
    public void Validate_UnsignedAssertion_IsRejected()
    {
        using var cert = SamlTestAssertions.CreateSigningCertificate();
        var validator = CreateValidator(SamlTestAssertions.ToBase64Der(cert));
        var response = Decode(SamlTestAssertions.CreateUnsignedResponse("user@example.com"));

        var result = validator.Validate(response);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.FailureReason);
    }

    [UnitTest]
    public void Validate_TamperedAssertion_IsRejected()
    {
        using var cert = SamlTestAssertions.CreateSigningCertificate();
        var validator = CreateValidator(SamlTestAssertions.ToBase64Der(cert));
        var response = Decode(SamlTestAssertions.CreateTamperedResponse(cert, "user@example.com"));

        var result = validator.Validate(response);

        Assert.False(result.Succeeded);
    }

    [UnitTest]
    public void Validate_SignedByDifferentKey_IsRejected()
    {
        using var signingCert = SamlTestAssertions.CreateSigningCertificate();
        using var otherCert = SamlTestAssertions.CreateSigningCertificate();
        // Server is configured to trust a DIFFERENT certificate than the one that signed.
        var validator = CreateValidator(SamlTestAssertions.ToBase64Der(otherCert));
        var response = Decode(SamlTestAssertions.CreateSignedResponse(
            signingCert, "user@example.com", "u@example.com", "U", ["viewer"]));

        var result = validator.Validate(response);

        Assert.False(result.Succeeded);
    }

    [UnitTest]
    public void Validate_ExpiredAssertion_IsRejected()
    {
        using var cert = SamlTestAssertions.CreateSigningCertificate();
        var validator = CreateValidator(SamlTestAssertions.ToBase64Der(cert), o => o.AllowedClockSkewSeconds = 0);
        var response = Decode(SamlTestAssertions.CreateSignedResponse(
            cert, "user@example.com", "u@example.com", "U", ["viewer"],
            notBefore: DateTimeOffset.UtcNow.AddMinutes(-30),
            notOnOrAfter: DateTimeOffset.UtcNow.AddMinutes(-10)));

        var result = validator.Validate(response);

        Assert.False(result.Succeeded);
        Assert.Contains("expired", result.FailureReason!, StringComparison.OrdinalIgnoreCase);
    }

    [UnitTest]
    public void Validate_WrongAudience_IsRejected()
    {
        using var cert = SamlTestAssertions.CreateSigningCertificate();
        var validator = CreateValidator(SamlTestAssertions.ToBase64Der(cert), o => o.EntityId = "https://someone-else.example.com/sp");
        var response = Decode(SamlTestAssertions.CreateSignedResponse(
            cert, "user@example.com", "u@example.com", "U", ["viewer"]));

        var result = validator.Validate(response);

        Assert.False(result.Succeeded);
        Assert.Contains("audience", result.FailureReason!, StringComparison.OrdinalIgnoreCase);
    }

    [UnitTest]
    public void Validate_WrongIssuer_IsRejected()
    {
        using var cert = SamlTestAssertions.CreateSigningCertificate();
        var validator = CreateValidator(SamlTestAssertions.ToBase64Der(cert), o => o.IdpEntityId = "https://wrong-issuer.example.com");
        var response = Decode(SamlTestAssertions.CreateSignedResponse(
            cert, "user@example.com", "u@example.com", "U", ["viewer"]));

        var result = validator.Validate(response);

        Assert.False(result.Succeeded);
        Assert.Contains("issuer", result.FailureReason!, StringComparison.OrdinalIgnoreCase);
    }
}
