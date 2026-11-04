// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Honua.Geocoding.Features.Geocoding.Domain;

public sealed record GeocodeMagicKey(string Text, string? Category, string? Provider);

// The magicKey HMAC is an integrity check, not a secret: the payload is only the suggestion text the caller already typed.
public static class GeocodeMagicKeyCodec
{
    private const byte Version = 1;
    private static readonly char[] FieldSeparator = ['\t'];

    // Fixed, well-known signing key. The signature provides tamper-evidence/opacity, not secrecy
    // (the GeocodeServer is anonymous and the payload echoes the caller's own suggestion text), and
    // it must be deterministic across processes/nodes so a token minted on one host resolves on any
    // host. Do not derive this from a per-process secret or determinism breaks across restarts.
    private static readonly byte[] SigningKey = "honua.geocode.magickey.v1"u8.ToArray();

    public static string Encode(GeocodeMagicKey magicKey)
    {
        ArgumentNullException.ThrowIfNull(magicKey);

        var payload = BuildPayload(magicKey);
        var signature = HMACSHA256.HashData(SigningKey, payload);

        return string.Concat(ToBase64Url(payload), ".", ToBase64Url(signature));
    }

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
