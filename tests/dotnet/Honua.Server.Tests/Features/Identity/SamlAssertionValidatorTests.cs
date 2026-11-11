// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using Honua.Infrastructure.Security;
using Honua.Server.Features.Identity.Saml;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Options;
using NSubstitute;

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
            AssertionConsumerServiceUrl = SamlTestAssertions.AcsUrl,
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

    public static TheoryData<string[][]> ExcludingAudienceGroups => new()
    {
        new string[][] { [SamlTestAssertions.Audience, "https://other.example.com/sp"], ["https://other.example.com/sp"] },
        new string[][] { ["https://other.example.com/sp"], [SamlTestAssertions.Audience, "https://other.example.com/sp"] }
    };

    [UnitTheory]
    [MemberData(nameof(ExcludingAudienceGroups))]
    public void Validate_AudienceRestrictionExcludesServiceProvider_IsRejected(string[][] groups)
    {
        using var cert = SamlTestAssertions.CreateSigningCertificate();
        var validator = CreateValidator(SamlTestAssertions.ToBase64Der(cert));

        var result = validator.Validate(SamlTestAssertions.CreateSignedResponseWithAudienceGroups(cert, groups));

        Assert.False(result.Succeeded);
        Assert.Contains("audience", result.FailureReason);
    }

    [UnitTest]
    public void Validate_EveryAudienceRestrictionPermitsServiceProvider_Succeeds()
    {
        using var cert = SamlTestAssertions.CreateSigningCertificate();
        var validator = CreateValidator(SamlTestAssertions.ToBase64Der(cert));
        var response = SamlTestAssertions.CreateSignedResponseWithAudienceGroups(cert,
            [["https://other.example.com/sp", SamlTestAssertions.Audience], [SamlTestAssertions.Audience]]);

        var result = validator.Validate(response);

        Assert.True(result.Succeeded, result.FailureReason);
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

    [UnitTest]
    public void Validate_ReplayOfAssertionId_IsRejected()
    {
        using var cert = SamlTestAssertions.CreateSigningCertificate();
        var validator = CreateValidator(SamlTestAssertions.ToBase64Der(cert));
        var response = Decode(SamlTestAssertions.CreateSignedResponse(
            cert, "user@example.com", "user@example.com", "Display Name", ["viewer"]));

        var first = validator.Validate(response);
        var second = validator.Validate(response);

        Assert.True(first.Succeeded, first.FailureReason);
        Assert.False(second.Succeeded);
        Assert.Contains("replay", second.FailureReason!, StringComparison.OrdinalIgnoreCase);
    }

    [UnitTest]
    public void Validate_MissingConditions_IsRejected()
    {
        using var cert = SamlTestAssertions.CreateSigningCertificate();
        var validator = CreateValidator(SamlTestAssertions.ToBase64Der(cert));
        var response = Decode(SamlTestAssertions.CreateSignedResponse(
            cert, "user@example.com", "u@example.com", "U", ["viewer"], includeConditions: false));

        var result = validator.Validate(response);

        Assert.False(result.Succeeded);
        Assert.Contains("Conditions", result.FailureReason!, StringComparison.Ordinal);
    }

    [UnitTest]
    public void Validate_MissingNotOnOrAfter_IsRejected()
    {
        using var cert = SamlTestAssertions.CreateSigningCertificate();
        var validator = CreateValidator(SamlTestAssertions.ToBase64Der(cert));
        var response = Decode(SamlTestAssertions.CreateSignedResponse(
            cert, "user@example.com", "u@example.com", "U", ["viewer"], includeNotOnOrAfter: false));

        var result = validator.Validate(response);

        Assert.False(result.Succeeded);
        Assert.Contains("NotOnOrAfter", result.FailureReason!, StringComparison.Ordinal);
    }

    [UnitTest]
    public void Validate_MissingAudience_IsRejected()
    {
        using var cert = SamlTestAssertions.CreateSigningCertificate();
        var validator = CreateValidator(SamlTestAssertions.ToBase64Der(cert));
        var response = Decode(SamlTestAssertions.CreateSignedResponse(
            cert, "user@example.com", "u@example.com", "U", ["viewer"], includeAudience: false));

        var result = validator.Validate(response);

        Assert.False(result.Succeeded);
        Assert.Contains("audience", result.FailureReason!, StringComparison.OrdinalIgnoreCase);
    }

    [UnitTest]
    public void Validate_WrongRecipientOrDestination_IsRejected()
    {
        using var cert = SamlTestAssertions.CreateSigningCertificate();
        var validator = CreateValidator(SamlTestAssertions.ToBase64Der(cert));
        var wrongRecipient = Decode(SamlTestAssertions.CreateSignedResponse(
            cert, "user@example.com", "u@example.com", "U", ["viewer"],
            recipient: "https://attacker.example/acs"));
        var missingDestination = Decode(SamlTestAssertions.CreateSignedResponse(
            cert, "user@example.com", "u@example.com", "U", ["viewer"], includeDestination: false));

        Assert.False(validator.Validate(wrongRecipient).Succeeded);
        Assert.False(validator.Validate(missingDestination).Succeeded);
    }

    [UnitTest]
    public void Validate_EmptyServiceProviderSettings_FailsClosed()
    {
        using var cert = SamlTestAssertions.CreateSigningCertificate();
        var validator = CreateValidator(SamlTestAssertions.ToBase64Der(cert), options => options.EntityId = "");
        var response = Decode(SamlTestAssertions.CreateSignedResponse(
            cert, "user@example.com", "u@example.com", "U", ["viewer"]));

        var result = validator.Validate(response);

        Assert.False(result.Succeeded);
        Assert.Contains("not fully configured", result.FailureReason!, StringComparison.OrdinalIgnoreCase);
    }

    [UnitTest]
    public void Validate_MismatchedInResponseTo_IsRejected_AndAbsentPairSucceeds()
    {
        using var cert = SamlTestAssertions.CreateSigningCertificate();
        var validator = CreateValidator(SamlTestAssertions.ToBase64Der(cert));
        var mismatched = Decode(SamlTestAssertions.CreateSignedResponse(
            cert, "user@example.com", "u@example.com", "U", ["viewer"],
            responseInResponseTo: "_req-a",
            confirmationInResponseTo: "_req-b"));
        var absent = Decode(SamlTestAssertions.CreateSignedResponse(
            cert, "idp-initiated@example.com", "u@example.com", "U", ["viewer"]));

        var mismatch = validator.Validate(mismatched);
        var idpInitiated = validator.Validate(absent);

        Assert.False(mismatch.Succeeded);
        Assert.Contains("InResponseTo", mismatch.FailureReason!, StringComparison.Ordinal);
        Assert.True(idpInitiated.Succeeded, idpInitiated.FailureReason);
    }

    [UnitTest]
    public void Validate_PrefixDeclaredOnlyOnResponse_Succeeds()
    {
        using var cert = SamlTestAssertions.CreateSigningCertificate();
        var validator = CreateValidator(SamlTestAssertions.ToBase64Der(cert));
        var response = Decode(SamlTestAssertions.CreateSignedPrefixedAncestorResponse(cert, "prefixed@example.com"));

        var result = validator.Validate(response);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal("prefixed@example.com", result.Subject!.NameId);
        Assert.Contains("viewer", result.Subject.Roles);
    }

    [UnitTest]
    public void OptionsValidator_EnabledWithoutAssertionConsumer_Fails()
    {
        var result = new SamlAuthenticationOptionsValidator().Validate(
            name: null,
            new SamlAuthenticationOptions
            {
                Enabled = true,
                EntityId = SamlTestAssertions.Audience,
                IdpEntityId = SamlTestAssertions.Issuer,
            });

        Assert.False(result.Succeeded);
    }

    [UnitTest]
    public void ReplayCache_StoreUnavailable_RejectsTheAssertion()
    {
        var replays = Substitute.For<ITokenReplayStore>();
        replays.TryRegister(default).ReturnsForAnyArgs(_ => throw new TimeoutException("database timeout"));
        var cache = new SamlAssertionReplayCache(replays);

        Assert.False(cache.TryRemember("assertion-1", DateTimeOffset.UtcNow.AddMinutes(5)));
        Assert.False(cache.TryRemember("assertion-1", DateTimeOffset.UtcNow.AddMinutes(5)));
    }

    [UnitTest]
    public void ReplayCache_SecondPresentation_IsRejected()
    {
        var cache = new SamlAssertionReplayCache(new InMemoryTokenReplayStore());

        Assert.True(cache.TryRemember("assertion-1", DateTimeOffset.UtcNow.AddMinutes(5)));
        Assert.False(cache.TryRemember("assertion-1", DateTimeOffset.UtcNow.AddMinutes(5)));
    }

    [UnitTest]
    public async Task ReplayCache_ConcurrentFirstUse_OnlyOneClaimSucceeds()
    {
        var replays = new InMemoryTokenReplayStore();
        var expiry = DateTimeOffset.UtcNow.AddMinutes(5);

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            new SamlAssertionReplayCache(replays).TryRemember("concurrent-assertion", expiry))));

        Assert.Single(results, accepted => accepted);
    }
}
