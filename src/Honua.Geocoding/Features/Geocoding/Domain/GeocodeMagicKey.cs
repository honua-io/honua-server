// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Honua.Geocoding.Features.Geocoding.Domain;

/// <summary>
/// The identity a GeoServices <c>suggest</c> magicKey carries so that a later
/// <c>findAddressCandidates</c> call can resolve the exact suggestion the user picked.
/// </summary>
/// <remarks>
/// Esri locators mint an opaque <c>magicKey</c> per suggestion and resolve it server-side.
/// Honua's backing providers (Nominatim, Azure Maps, Amazon Location) each surface a
/// provider-internal id (an OSM place id, an Azure result id, an Amazon <c>PlaceId</c>) that
/// is <em>not</em> resolvable through their public forward-geocode endpoints, so there is no
/// portable provider id round-trip. Instead Honua self-issues a deterministic, signed, opaque
/// token (see <see cref="GeocodeMagicKeyCodec"/>) that encodes the suggestion's stable identity
/// — its display <see cref="Text"/>, optional <see cref="Category"/>, and originating
/// <see cref="Provider"/>. Resolving the magicKey re-runs a forward geocode for that text against
/// the same provider and applies the same category filter, which deterministically reproduces the
/// suggested place. The same suggestion always encodes to the same token, and a token always
/// resolves to the same query, so the round-trip is deterministic and provider-agnostic.
/// </remarks>
/// <param name="Text">The suggestion display text (the resolvable query).</param>
/// <param name="Category">Optional category/address-type the suggestion was classified as.</param>
/// <param name="Provider">The provider that issued the suggestion.</param>
public sealed record GeocodeMagicKey(string Text, string? Category, string? Provider);

/// <summary>
/// Encodes and decodes self-issued GeoServices <c>magicKey</c> tokens. The token is a signed,
/// opaque, URL-safe string that deterministically round-trips a <see cref="GeocodeMagicKey"/>.
/// </summary>
/// <remarks>
/// Wire format: <c>{base64url(payload)}.{base64url(HMACSHA256(payload))}</c> where
/// <c>payload</c> is a tab-delimited <c>version\ttext\tcategory\tprovider</c> UTF-8 record.
/// The HMAC makes the token tamper-evident and opaque (clients must not parse it), while a fixed
/// signing key keeps encoding deterministic across processes so a magicKey minted by one node
/// resolves on any node. This is intentionally an integrity check, not a secrecy mechanism: the
/// GeocodeServer surface is anonymous and read-only, and the payload only contains the suggestion
/// text the caller already typed.
/// </remarks>
public static class GeocodeMagicKeyCodec
{
    private const byte Version = 1;
    private static readonly char[] FieldSeparator = ['\t'];

    // Fixed, well-known signing key. The signature provides tamper-evidence/opacity, not secrecy
    // (the GeocodeServer is anonymous and the payload echoes the caller's own suggestion text), and
    // it must be deterministic across processes/nodes so a token minted on one host resolves on any
    // host. Do not derive this from a per-process secret or determinism breaks across restarts.
    private static readonly byte[] SigningKey = "honua.geocode.magickey.v1"u8.ToArray();

    /// <summary>
    /// Encodes a <see cref="GeocodeMagicKey"/> into a deterministic, signed, opaque token.
    /// </summary>
    /// <param name="magicKey">The suggestion identity to encode.</param>
    /// <returns>An opaque URL-safe magicKey token.</returns>
    public static string Encode(GeocodeMagicKey magicKey)
    {
        ArgumentNullException.ThrowIfNull(magicKey);

        var payload = BuildPayload(magicKey);
        var signature = HMACSHA256.HashData(SigningKey, payload);

        return string.Concat(ToBase64Url(payload), ".", ToBase64Url(signature));
    }

    /// <summary>
    /// Attempts to decode an opaque magicKey token back into a <see cref="GeocodeMagicKey"/>.
    /// Returns <see langword="false"/> for a malformed, truncated, or signature-mismatched token.
    /// </summary>
    /// <param name="token">The opaque magicKey token to decode.</param>
    /// <param name="magicKey">The decoded suggestion identity when the token is valid.</param>
    /// <returns><see langword="true"/> when the token decodes and its signature verifies.</returns>
    public static bool TryDecode(string? token, out GeocodeMagicKey? magicKey)
    {
        magicKey = null;

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var separatorIndex = token.IndexOf('.');
        if (separatorIndex <= 0 || separatorIndex >= token.Length - 1)
        {
            return false;
        }

        if (!TryFromBase64Url(token.AsSpan(0, separatorIndex), out var payload) ||
            !TryFromBase64Url(token.AsSpan(separatorIndex + 1), out var signature))
        {
            return false;
        }

        var expected = HMACSHA256.HashData(SigningKey, payload);
        if (!CryptographicOperations.FixedTimeEquals(signature, expected))
        {
            return false;
        }

        var text = Encoding.UTF8.GetString(payload);
        var fields = text.Split(FieldSeparator);
        if (fields.Length != 4 ||
            !byte.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var version) ||
            version != Version)
        {
            return false;
        }

        var suggestionText = fields[1];
        if (string.IsNullOrEmpty(suggestionText))
        {
            return false;
        }

        magicKey = new GeocodeMagicKey(
            suggestionText,
            string.IsNullOrEmpty(fields[2]) ? null : fields[2],
            string.IsNullOrEmpty(fields[3]) ? null : fields[3]);
        return true;
    }

    private static byte[] BuildPayload(GeocodeMagicKey magicKey)
    {
        // Tab-delimited record: a tab cannot appear in the source fields after normalization, so
        // there is no ambiguity. Newlines/tabs in the text are stripped to keep the format stable.
        var record = string.Join(
            '\t',
            Version.ToString(CultureInfo.InvariantCulture),
            Sanitize(magicKey.Text),
            Sanitize(magicKey.Category),
            Sanitize(magicKey.Provider));

        return Encoding.UTF8.GetBytes(record);
    }

    private static string Sanitize(string? value)
        => string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

    private static string ToBase64Url(ReadOnlySpan<byte> bytes)
    {
        // URL-safe, unpadded base64 so the token is safe in query strings without escaping.
        var maxLength = Base64.GetMaxEncodedToUtf8Length(bytes.Length);
        Span<byte> buffer = maxLength <= 256 ? stackalloc byte[maxLength] : new byte[maxLength];
        Base64.EncodeToUtf8(bytes, buffer, out _, out var written);

        Span<char> chars = written <= 256 ? stackalloc char[written] : new char[written];
        var count = 0;
        for (var i = 0; i < written; i++)
        {
            var c = (char)buffer[i];
            switch (c)
            {
                case '+':
                    chars[count++] = '-';
                    break;
                case '/':
                    chars[count++] = '_';
                    break;
                case '=':
                    break;
                default:
                    chars[count++] = c;
                    break;
            }
        }

        return new string(chars[..count]);
    }

    private static bool TryFromBase64Url(ReadOnlySpan<char> value, out byte[] bytes)
    {
        bytes = [];
        if (value.IsEmpty)
        {
            return false;
        }

        var padded = (4 - (value.Length % 4)) % 4;
        var totalLength = value.Length + padded;
        Span<char> buffer = totalLength <= 512 ? stackalloc char[totalLength] : new char[totalLength];

        for (var i = 0; i < value.Length; i++)
        {
            buffer[i] = value[i] switch
            {
                '-' => '+',
                '_' => '/',
                var c => c
            };
        }

        for (var i = 0; i < padded; i++)
        {
            buffer[value.Length + i] = '=';
        }

        var output = new byte[(totalLength / 4) * 3];
        if (Convert.TryFromBase64Chars(buffer, output, out var written))
        {
            bytes = written == output.Length ? output : output[..written];
            return true;
        }

        return false;
    }
}
