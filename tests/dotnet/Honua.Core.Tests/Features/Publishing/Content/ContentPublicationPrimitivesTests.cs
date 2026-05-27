// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Publishing.Content.Domain;
using Honua.Core.Features.Publishing.Content.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Core.Tests.Features.Publishing.Content;

/// <summary>
/// Unit tests for content publication primitives: slug normalization, content/token
/// hashing, and public-link verification.
/// </summary>
[Protocol(Protocols.TestQuality)]
public sealed class ContentPublicationPrimitivesTests
{
    [Operation(Operations.Query)]
    [Theory]
    [InlineData("My Quarterly Map", "my-quarterly-map")]
    [InlineData("  Trailing/Leading  ", "trailing/leading")]
    [InlineData("UPPER__double--dash", "upper-double-dash")]
    [InlineData("maps/Quarterly Report", "maps/quarterly-report")]
    [InlineData("/leading/slash/", "leading/slash")]
    public void TryNormalize_ProducesCanonicalSlug(string input, string expected)
    {
        ContentPublicationSlug.TryNormalize(input, out var slug).Should().BeTrue();
        slug.Should().Be(expected);
    }

    [Operation(Operations.Query)]
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../etc/passwd")]
    [InlineData("!!!")]
    public void TryNormalize_RejectsInvalidSlugs(string? input)
    {
        ContentPublicationSlug.TryNormalize(input, out _).Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void Normalize_IsIdempotentOnCanonicalSlug()
    {
        var once = ContentPublicationSlug.Normalize("Quarterly Sales/Map");
        var twice = ContentPublicationSlug.Normalize(once);
        twice.Should().Be(once);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void Normalize_FallsBackToTitleThenGeneratedId()
    {
        ContentPublicationSlug.Normalize(null, "Fallback Title").Should().Be("fallback-title");
        ContentPublicationSlug.Normalize(null, null).Should().StartWith("pub-");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void Sha256Hex_IsDeterministicAndLowercaseHex()
    {
        var a = ContentPublicationCrypto.Sha256Hex("payload");
        var b = ContentPublicationCrypto.Sha256Hex("payload");
        a.Should().Be(b);
        a.Should().MatchRegex("^[0-9a-f]{64}$");
        ContentPublicationCrypto.Sha256Hex("other").Should().NotBe(a);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TokenHash_RoundTripsWithoutStoringRawToken()
    {
        const string token = "super-secret-token";
        var hash = ContentPublicationCrypto.HashToken(token);

        hash.Should().NotContain(token);
        ContentPublicationCrypto.TokenMatchesHash(token, hash).Should().BeTrue();
        ContentPublicationCrypto.TokenMatchesHash("wrong", hash).Should().BeFalse();
        ContentPublicationCrypto.TokenMatchesHash(token, "not-base64!!").Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void NewEtag_IsQuotedAndUnique()
    {
        var first = ContentPublicationCrypto.NewEtag();
        var second = ContentPublicationCrypto.NewEtag();
        first.Should().StartWith("\"").And.EndWith("\"");
        first.Should().NotBe(second);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void PublicLinkVerifier_EnforcesEnabledRevokedExpiredAndToken()
    {
        var now = DateTimeOffset.UtcNow;
        var tokenHash = ContentPublicationCrypto.HashToken("link-token");

        var enabledPolicy = new ContentPublicLinkPolicy
        {
            Enabled = true,
            Links =
            [
                new ContentPublicLink { LinkId = "open", CreatedBy = "u", CreatedAt = now },
                new ContentPublicLink { LinkId = "tokened", TokenHash = tokenHash, TokenHashAlgorithm = "SHA-256", CreatedBy = "u", CreatedAt = now },
                new ContentPublicLink { LinkId = "revoked", Revoked = true, CreatedBy = "u", CreatedAt = now },
                new ContentPublicLink { LinkId = "expired", CreatedBy = "u", CreatedAt = now.AddHours(-2), ExpiresAt = now.AddHours(-1) },
            ],
        };

        // Disabled policy denies everything.
        ContentPublicLinkVerifier.TryAuthorize(enabledPolicy with { Enabled = false }, "open", null, now).Should().BeFalse();

        // Token-free link authorizes by id alone.
        ContentPublicLinkVerifier.TryAuthorize(enabledPolicy, "open", null, now).Should().BeTrue();

        // Unknown link id denied.
        ContentPublicLinkVerifier.TryAuthorize(enabledPolicy, "missing", null, now).Should().BeFalse();

        // Tokened link requires the matching token.
        ContentPublicLinkVerifier.TryAuthorize(enabledPolicy, "tokened", null, now).Should().BeFalse();
        ContentPublicLinkVerifier.TryAuthorize(enabledPolicy, "tokened", "wrong", now).Should().BeFalse();
        ContentPublicLinkVerifier.TryAuthorize(enabledPolicy, "tokened", "link-token", now).Should().BeTrue();

        // Revoked and expired links denied.
        ContentPublicLinkVerifier.TryAuthorize(enabledPolicy, "revoked", null, now).Should().BeFalse();
        ContentPublicLinkVerifier.TryAuthorize(enabledPolicy, "expired", null, now).Should().BeFalse();
    }
}
