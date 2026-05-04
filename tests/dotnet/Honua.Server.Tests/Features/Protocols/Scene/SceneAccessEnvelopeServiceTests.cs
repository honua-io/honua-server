// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Protocols.Scene;
using Honua.Server.Features.Protocols.Scene.Models;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Protocols.Scene;

/// <summary>
/// Unit tests for the HMAC-SHA256 scene access envelope service. The service
/// is a pure function over <c>(sceneId, expiresAt, signingKey)</c>; these
/// tests verify each negative path (tamper, expiry, wrong scene) without
/// spinning up a web host.
/// </summary>
[Protocol(TestProtocols.Scene)]
public sealed class SceneAccessEnvelopeServiceTests
{
    private const string SigningKey = "test-signing-key-please-rotate-aHcUZ4tQwT8a";
    private const string SceneId = "fixture-scene";
    private static readonly string[] ExpectedAllowedMethods = ["GET", "HEAD"];

    private static SceneAccessEnvelopeService CreateService(
        TestTimeProvider? timeProvider = null,
        int? ttlMinutes = null,
        string? signingKey = null)
    {
        var options = Options.Create(new SceneAccessSigningOptions
        {
            SigningKey = signingKey ?? SigningKey,
            TokenTtlMinutes = ttlMinutes ?? SceneAccessSigningOptions.DefaultTokenTtlMinutes,
            RefreshAfterFractionOfTtl = SceneAccessSigningOptions.DefaultRefreshAfterFraction
        });
        return new SceneAccessEnvelopeService(options, timeProvider ?? new TestTimeProvider());
    }

    [UnitTest]
    public void Issue_ReturnsEnvelopeWithExpectedScopeAndExpiry()
    {
        var time = new TestTimeProvider();
        var service = CreateService(time, ttlMinutes: 30);

        var envelope = service.Issue(SceneId);

        envelope.SceneId.Should().Be(SceneId);
        envelope.Token.Should().NotBeNullOrEmpty();
        envelope.Token.Should().Contain(".", "wire format separates payload and signature with a literal '.'");
        envelope.ExpiresAt.Should().Be(time.GetUtcNow().AddMinutes(30));
        envelope.RefreshAfter.Should().Be(time.GetUtcNow().AddMinutes(15));
        envelope.AllowedMethods.Should().BeEquivalentTo(ExpectedAllowedMethods);
    }

    [UnitTest]
    public void Issue_TokenStringEncodingIsRedactedInToString()
    {
        var service = CreateService();
        var envelope = service.Issue(SceneId);

        // Confidence guard: the response object must never embed a token in
        // its default string representation, so accidental string interpolation
        // in a log call cannot leak the credential.
        envelope.ToString().Should().NotContain(envelope.Token);
        envelope.ToString().Should().Contain("[redacted]");
    }

    [UnitTest]
    public void Validate_HappyPath_ReturnsAllowed()
    {
        var service = CreateService();
        var envelope = service.Issue(SceneId);

        var result = service.Validate(envelope.Token, SceneId);

        result.Should().Be(EnvelopeValidationResult.Allowed);
    }

    [UnitTest]
    public void Validate_ExpiredToken_ReturnsExpired()
    {
        var time = new TestTimeProvider();
        var service = CreateService(time, ttlMinutes: 5);
        var envelope = service.Issue(SceneId);

        time.Advance(TimeSpan.FromMinutes(6));

        var result = service.Validate(envelope.Token, SceneId);

        result.Should().Be(EnvelopeValidationResult.Expired);
    }

    [UnitTest]
    public void Validate_TamperedSignature_ReturnsTampered()
    {
        var service = CreateService();
        var envelope = service.Issue(SceneId);

        // Flip the last hex digit of the signature.
        var tampered = envelope.Token[..^1] + (envelope.Token[^1] == '0' ? '1' : '0');

        var result = service.Validate(tampered, SceneId);

        result.Should().Be(EnvelopeValidationResult.Tampered);
    }

    [UnitTest]
    public void Validate_TamperedPayload_ReturnsTampered()
    {
        var service = CreateService();
        var envelope = service.Issue(SceneId);

        // Mutate the payload (left of the '.' separator). The HMAC will no
        // longer cover the modified bytes so verification must fail before
        // any field check.
        var dotIndex = envelope.Token.LastIndexOf('.');
        var payload = envelope.Token[..dotIndex];
        var signature = envelope.Token[dotIndex..];
        // Replace one base64url char with a different valid char.
        var first = payload[0];
        var swapped = first == 'A' ? 'B' : 'A';
        var tampered = swapped + payload[1..] + signature;

        var result = service.Validate(tampered, SceneId);

        result.Should().Be(EnvelopeValidationResult.Tampered);
    }

    [UnitTest]
    public void Validate_DifferentSigningKey_ReturnsTampered()
    {
        // Issuer used a different key than the verifier. From the verifier's
        // perspective this is indistinguishable from a forged signature.
        var issuer = CreateService(signingKey: "alternate-signing-key-3xMq9pQ7vY2nE6");
        var verifier = CreateService();

        var envelope = issuer.Issue(SceneId);
        var result = verifier.Validate(envelope.Token, SceneId);

        result.Should().Be(EnvelopeValidationResult.Tampered);
    }

    [UnitTest]
    public void Validate_WrongScene_ReturnsWrongScene()
    {
        var service = CreateService();
        var envelope = service.Issue(SceneId);

        var result = service.Validate(envelope.Token, "different-scene");

        result.Should().Be(EnvelopeValidationResult.WrongScene);
    }

    [UnitTest]
    public void Validate_MissingToken_ReturnsTampered()
    {
        var service = CreateService();

        service.Validate(string.Empty, SceneId)
            .Should().Be(EnvelopeValidationResult.Tampered);
        service.Validate(null!, SceneId)
            .Should().Be(EnvelopeValidationResult.Tampered);
        service.Validate("not-a-valid-token-format", SceneId)
            .Should().Be(EnvelopeValidationResult.Tampered);
    }

    [UnitTest]
    public void Validate_GarbledBase64Url_ReturnsTampered()
    {
        var service = CreateService();

        var result = service.Validate("!!!.deadbeef", SceneId);

        result.Should().Be(EnvelopeValidationResult.Tampered);
    }

    [UnitTest]
    public void Constructor_ThrowsWhenSigningKeyMissing()
    {
        var options = Options.Create(new SceneAccessSigningOptions
        {
            SigningKey = string.Empty
        });
        Action act = () => _ = new SceneAccessEnvelopeService(options, new TestTimeProvider());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*SigningKey*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(SceneAccessSigningOptions.MaxTokenTtlMinutes + 1)]
    [Trait("Category", "Unit")]
    public void Constructor_ThrowsWhenTokenTtlMinutesOutOfRange(int ttlMinutes)
    {
        // Out-of-range TTL must throw InvalidOperationException so the scene
        // endpoints' catch (InvalidOperationException) blocks can surface a
        // structured 500 + OptionsMisconfigured log. Data-annotation validation
        // would throw OptionsValidationException, which the catch sites do not
        // intercept.
        var options = Options.Create(new SceneAccessSigningOptions
        {
            SigningKey = SigningKey,
            TokenTtlMinutes = ttlMinutes
        });
        Action act = () => _ = new SceneAccessEnvelopeService(options, new TestTimeProvider());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*TokenTtlMinutes*");
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    [Trait("Category", "Unit")]
    public void Constructor_ThrowsWhenRefreshFractionOutOfRange(double fraction)
    {
        var options = Options.Create(new SceneAccessSigningOptions
        {
            SigningKey = SigningKey,
            RefreshAfterFractionOfTtl = fraction
        });
        Action act = () => _ = new SceneAccessEnvelopeService(options, new TestTimeProvider());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*RefreshAfterFractionOfTtl*");
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 5, 3, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
