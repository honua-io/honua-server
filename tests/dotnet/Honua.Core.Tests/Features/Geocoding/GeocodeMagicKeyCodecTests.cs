// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Geocoding.Features.Geocoding.Domain;

namespace Honua.Core.Tests.Features.Geocoding;

public sealed class GeocodeMagicKeyCodecTests
{
    [Fact]
    public void Encode_ThenDecode_RoundTripsAllFields()
    {
        var original = new GeocodeMagicKey("1600 Pennsylvania Ave NW, Washington, DC", "PointAddress", "nominatim");

        var token = GeocodeMagicKeyCodec.Encode(original);
        var decoded = GeocodeMagicKeyCodec.TryDecode(token, out var result);

        Assert.True(decoded);
        Assert.NotNull(result);
        Assert.Equal(original.Text, result!.Text);
        Assert.Equal(original.Category, result.Category);
        Assert.Equal(original.Provider, result.Provider);
    }

    [Fact]
    public void Encode_IsDeterministic_SameInputProducesSameToken()
    {
        var magicKey = new GeocodeMagicKey("Honua HQ", "POI", "azuremaps");

        var first = GeocodeMagicKeyCodec.Encode(magicKey);
        var second = GeocodeMagicKeyCodec.Encode(magicKey);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Encode_DifferentSuggestions_ProduceDifferentTokens()
    {
        var a = GeocodeMagicKeyCodec.Encode(new GeocodeMagicKey("350 Fifth Avenue", "PointAddress", "nominatim"));
        var b = GeocodeMagicKeyCodec.Encode(new GeocodeMagicKey("351 Fifth Avenue", "PointAddress", "nominatim"));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Encode_ProducesUrlSafeToken()
    {
        var token = GeocodeMagicKeyCodec.Encode(
            new GeocodeMagicKey("Café Müller, Île-de-France", "POI", "amazonlocation"));

        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.DoesNotContain('=', token);
        // Decodes back to the original despite non-ASCII content.
        Assert.True(GeocodeMagicKeyCodec.TryDecode(token, out var decoded));
        Assert.Equal("Café Müller, Île-de-France", decoded!.Text);
    }

    [Fact]
    public void TryDecode_RoundTripsWhenCategoryAndProviderAreNull()
    {
        var token = GeocodeMagicKeyCodec.Encode(new GeocodeMagicKey("Springfield", null, null));

        Assert.True(GeocodeMagicKeyCodec.TryDecode(token, out var decoded));
        Assert.Equal("Springfield", decoded!.Text);
        Assert.Null(decoded.Category);
        Assert.Null(decoded.Provider);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-token")]
    [InlineData("no-dot-separator")]
    [InlineData(".onlysignature")]
    [InlineData("onlypayload.")]
    public void TryDecode_RejectsMalformedTokens(string? token)
    {
        Assert.False(GeocodeMagicKeyCodec.TryDecode(token, out var decoded));
        Assert.Null(decoded);
    }

    [Fact]
    public void TryDecode_RejectsTamperedPayload()
    {
        var token = GeocodeMagicKeyCodec.Encode(new GeocodeMagicKey("Original Street", "PointAddress", "nominatim"));
        var dotIndex = token.IndexOf('.');

        // Flip a character in the payload portion; the signature no longer matches.
        var payload = token[..dotIndex];
        var signature = token[(dotIndex + 1)..];
        var flippedChar = payload[0] == 'A' ? 'B' : 'A';
        var tampered = $"{flippedChar}{payload[1..]}.{signature}";

        Assert.False(GeocodeMagicKeyCodec.TryDecode(tampered, out var decoded));
        Assert.Null(decoded);
    }

    [Fact]
    public void TryDecode_RejectsTamperedSignature()
    {
        var token = GeocodeMagicKeyCodec.Encode(new GeocodeMagicKey("Original Street", "PointAddress", "nominatim"));
        var dotIndex = token.IndexOf('.');
        var payload = token[..dotIndex];

        Assert.False(GeocodeMagicKeyCodec.TryDecode(string.Concat(payload, ".AAAA"), out var decoded));
        Assert.Null(decoded);
    }
}
