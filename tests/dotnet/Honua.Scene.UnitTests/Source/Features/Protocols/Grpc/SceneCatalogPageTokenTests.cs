// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Scene.Grpc;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

/// <summary>
/// Unit tests for the opaque <c>ListScenes</c> continuation token introduced when
/// Geospatial.Grpc 0.2.0-alpha.1 retired <c>result_offset</c> / <c>result_record_count</c>
/// on <c>ListScenesRequest</c> in favour of <c>page_size</c> + <c>page_token</c>.
/// </summary>
public sealed class SceneCatalogPageTokenTests
{
    [UnitTest]
    public void Encode_ThenDecode_RoundTripsOffset()
    {
        foreach (var offset in new[] { 0, 1, 42, 10_000, int.MaxValue })
        {
            var token = SceneCatalogPageToken.Encode(offset);

            SceneCatalogPageToken.TryDecode(token, out var decoded).Should().BeTrue();
            decoded.Should().Be(offset);
        }
    }

    [UnitTest]
    public void Encode_ProducesUrlSafeOpaqueToken()
    {
        // The token must not leak the raw offset as plain text and must survive being
        // carried in a header or a query string without escaping.
        var token = SceneCatalogPageToken.Encode(7);

        token.Should().NotBe("7");
        token.Should().NotContain("+");
        token.Should().NotContain("/");
        token.Should().NotContain("=");
    }

    [UnitTest]
    public void Encode_NegativeOffset_Throws()
    {
        var act = () => SceneCatalogPageToken.Encode(-1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [UnitTest]
    public void TryDecode_EmptyToken_IsFirstPage()
    {
        // An empty page_token is the documented "give me the first page" request, not an error.
        SceneCatalogPageToken.TryDecode(null, out var fromNull).Should().BeTrue();
        fromNull.Should().Be(0);

        SceneCatalogPageToken.TryDecode(string.Empty, out var fromEmpty).Should().BeTrue();
        fromEmpty.Should().Be(0);
    }

    [UnitTest]
    public void TryDecode_ForeignToken_IsRejected()
    {
        // A token this server did not mint must be rejected rather than silently read as
        // offset 0, so a client paging against the wrong server sees an error.
        SceneCatalogPageToken.TryDecode("not-a-token", out _).Should().BeFalse();

        // Well-formed base64url whose payload is not a scene cursor.
        var foreign = Convert.ToBase64String("style:v1:5"u8.ToArray())
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        SceneCatalogPageToken.TryDecode(foreign, out _).Should().BeFalse();
    }

    [UnitTest]
    public void TryDecode_NonNumericPayload_IsRejected()
    {
        var malformed = Convert.ToBase64String("scene:v1:not-a-number"u8.ToArray())
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        SceneCatalogPageToken.TryDecode(malformed, out _).Should().BeFalse();
    }

    [UnitTest]
    public void TryDecode_SignedPayload_IsRejected()
    {
        // int.TryParse with NumberStyles.None must not accept a leading sign, so a hand-rolled
        // "scene:v1:-1" cannot smuggle a negative offset into Enumerable.Skip.
        var signed = Convert.ToBase64String("scene:v1:-1"u8.ToArray())
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        SceneCatalogPageToken.TryDecode(signed, out _).Should().BeFalse();
    }
}
