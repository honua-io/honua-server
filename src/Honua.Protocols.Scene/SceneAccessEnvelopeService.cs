// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Protocols.Scene.Models;
using Honua.Scene.Assets;
using Microsoft.Extensions.Options;

namespace Honua.Protocols.Scene;

/// <summary>
/// HMAC-SHA256 signed scene access envelope issuer/verifier.
/// </summary>
/// <remarks>
/// Wire format: <c>{base64url(payload_json)}.{hmac_sha256_hex}</c> where the
/// canonical signing string is <c>{sceneId}\n{expiresAtUnixSeconds}</c>. Scene
/// id is bound at the envelope (not asset path) level because a Cesium session
/// cascades through thousands of asset URLs under one scene root and per-asset
/// signing would require a server round-trip per tile. Path-traversal safety
/// is enforced by <see cref="SceneAssetResolver"/> further down the pipeline.
/// </remarks>
internal sealed class SceneAccessEnvelopeService : ISceneAccessEnvelopeService
{
    private static readonly IReadOnlyList<string> AllowedMethods = new[] { "GET", "HEAD" };

    private readonly TimeProvider _timeProvider;
    private readonly byte[] _signingKey;
    private readonly TimeSpan _ttl;
    private readonly double _refreshFraction;

    public SceneAccessEnvelopeService(
        IOptions<SceneAccessSigningOptions> options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        // All bounds checks throw InvalidOperationException so the scene
        // endpoints' catch (InvalidOperationException) blocks surface a
        // structured 500 + OptionsMisconfigured log on misconfigured
        // deployments. Data-annotation validation is deliberately not used —
        // it would throw OptionsValidationException, which the catch sites
        // do not intercept.
        var value = options.Value;
        if (string.IsNullOrEmpty(value.SigningKey))
        {
            throw new InvalidOperationException(
                "Honua:SceneAccessSigning:SigningKey is required to issue scene access envelopes.");
        }

        if (value.TokenTtlMinutes < SceneAccessSigningOptions.MinTokenTtlMinutes
            || value.TokenTtlMinutes > SceneAccessSigningOptions.MaxTokenTtlMinutes)
        {
            throw new InvalidOperationException(
                $"Honua:SceneAccessSigning:TokenTtlMinutes must be between "
                + $"{SceneAccessSigningOptions.MinTokenTtlMinutes} and "
                + $"{SceneAccessSigningOptions.MaxTokenTtlMinutes} minutes.");
        }

        if (double.IsNaN(value.RefreshAfterFractionOfTtl)
            || value.RefreshAfterFractionOfTtl < 0.0
            || value.RefreshAfterFractionOfTtl > 1.0)
        {
            throw new InvalidOperationException(
                "Honua:SceneAccessSigning:RefreshAfterFractionOfTtl must be between 0.0 and 1.0.");
        }

        _signingKey = Encoding.UTF8.GetBytes(value.SigningKey);
        _ttl = TimeSpan.FromMinutes(value.TokenTtlMinutes);
        _refreshFraction = value.RefreshAfterFractionOfTtl;
        _timeProvider = timeProvider;
    }

    public SceneAccessEnvelope Issue(string sceneId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sceneId);

        var now = _timeProvider.GetUtcNow();
        var expiresAt = now + _ttl;
        var refreshAfter = now + (_ttl * _refreshFraction);

        var token = SignToken(sceneId, expiresAt.ToUnixTimeSeconds());

        return new SceneAccessEnvelope(
            SceneId: sceneId,
            Token: token,
            ExpiresAt: expiresAt,
            RefreshAfter: refreshAfter,
            AllowedMethods: AllowedMethods);
    }

    public EnvelopeValidationResult Validate(string rawToken, string requestedSceneId)
    {
        if (string.IsNullOrEmpty(rawToken) || string.IsNullOrEmpty(requestedSceneId))
        {
            return EnvelopeValidationResult.Tampered;
        }

        // Split at the last '.' so the base64url payload (which never
        // contains '.') is preserved exactly.
        var separatorIndex = rawToken.LastIndexOf('.');
        if (separatorIndex <= 0 || separatorIndex >= rawToken.Length - 1)
        {
            return EnvelopeValidationResult.Tampered;
        }

        var payloadSegment = rawToken.AsSpan(0, separatorIndex);
        var signatureSegment = rawToken.AsSpan(separatorIndex + 1);

        if (!TryDecodeBase64Url(payloadSegment, out var payloadBytes))
        {
            return EnvelopeValidationResult.Tampered;
        }

        SceneAccessTokenPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize(
                payloadBytes,
                SceneAccessEnvelopeJsonContext.Default.SceneAccessTokenPayload);
        }
        catch (JsonException)
        {
            return EnvelopeValidationResult.Tampered;
        }

        if (payload is null || string.IsNullOrEmpty(payload.SceneId))
        {
            return EnvelopeValidationResult.Tampered;
        }

        // Verify HMAC before checking expiry or scene id so we never reveal
        // anything about a forged payload via a status-code oracle.
        if (!TryDecodeHexLower(signatureSegment, out var providedHash))
        {
            return EnvelopeValidationResult.Tampered;
        }

        Span<byte> expectedHash = stackalloc byte[32];
        if (!TryComputeHash(payload.SceneId, payload.ExpiresAtUnixSeconds, expectedHash))
        {
            return EnvelopeValidationResult.Tampered;
        }

        if (!CryptographicOperations.FixedTimeEquals(expectedHash, providedHash))
        {
            return EnvelopeValidationResult.Tampered;
        }

        var nowSeconds = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        if (payload.ExpiresAtUnixSeconds <= nowSeconds)
        {
            return EnvelopeValidationResult.Expired;
        }

        if (!string.Equals(payload.SceneId, requestedSceneId, StringComparison.Ordinal))
        {
            return EnvelopeValidationResult.WrongScene;
        }

        return EnvelopeValidationResult.Allowed;
    }

    private string SignToken(string sceneId, long expiresAtUnixSeconds)
    {
        var payload = new SceneAccessTokenPayload(sceneId, expiresAtUnixSeconds);
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(
            payload,
            SceneAccessEnvelopeJsonContext.Default.SceneAccessTokenPayload);

        var encodedPayload = EncodeBase64Url(payloadBytes);

        Span<byte> hash = stackalloc byte[32];
        if (!TryComputeHash(sceneId, expiresAtUnixSeconds, hash))
        {
            // Cannot happen: the signing string fits inside the inline
            // buffers and HMACSHA256 always produces 32 bytes.
            throw new InvalidOperationException("Failed to compute scene access envelope HMAC.");
        }

        return string.Create(
            encodedPayload.Length + 1 + (hash.Length * 2),
            (encodedPayload, hash: hash.ToArray()),
            static (span, state) =>
            {
                state.encodedPayload.AsSpan().CopyTo(span);
                span[state.encodedPayload.Length] = '.';
                WriteHexLower(state.hash, span[(state.encodedPayload.Length + 1)..]);
            });
    }

    private bool TryComputeHash(string sceneId, long expiresAtUnixSeconds, Span<byte> destination)
    {
        // Canonical signing string: "{sceneId}\n{expires}".
        // Bound stack allocation by the maximum reasonable scene-id length;
        // anything longer would have already been rejected upstream.
        var sceneIdByteCount = Encoding.UTF8.GetByteCount(sceneId);
        Span<byte> messageBuffer = sceneIdByteCount <= 256
            ? stackalloc byte[256 + 1 + 20]
            : new byte[sceneIdByteCount + 1 + 20];

        var written = Encoding.UTF8.GetBytes(sceneId, messageBuffer);
        messageBuffer[written++] = (byte)'\n';

        if (!Utf8Formatter.TryFormat(expiresAtUnixSeconds, messageBuffer[written..], out var expiresWritten))
        {
            return false;
        }

        var totalLength = written + expiresWritten;

        return HMACSHA256.TryHashData(_signingKey, messageBuffer[..totalLength], destination, out _);
    }

    private static string EncodeBase64Url(ReadOnlySpan<byte> bytes)
    {
        var base64 = Convert.ToBase64String(bytes);
        // Strip '=' padding and translate '+' / '/' for URL-safe encoding.
        var trimmedLength = base64.Length;
        while (trimmedLength > 0 && base64[trimmedLength - 1] == '=')
        {
            trimmedLength--;
        }

        return string.Create(trimmedLength, base64, static (span, source) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                var c = source[i];
                span[i] = c switch
                {
                    '+' => '-',
                    '/' => '_',
                    _ => c
                };
            }
        });
    }

    private static bool TryDecodeBase64Url(ReadOnlySpan<char> input, out byte[] decoded)
    {
        decoded = [];
        if (input.IsEmpty)
        {
            return false;
        }

        var paddedLength = input.Length + ((4 - (input.Length % 4)) % 4);
        Span<char> buffer = paddedLength <= 1024 ? stackalloc char[paddedLength] : new char[paddedLength];
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            buffer[i] = c switch
            {
                '-' => '+',
                '_' => '/',
                _ => c
            };
        }
        for (var i = input.Length; i < paddedLength; i++)
        {
            buffer[i] = '=';
        }

        var output = new byte[(paddedLength / 4) * 3];
        if (!Convert.TryFromBase64Chars(buffer, output, out var written))
        {
            return false;
        }

        if (written == output.Length)
        {
            decoded = output;
            return true;
        }

        decoded = output.AsSpan(0, written).ToArray();
        return true;
    }

    private static bool TryDecodeHexLower(ReadOnlySpan<char> input, out byte[] decoded)
    {
        decoded = [];
        if (input.IsEmpty || (input.Length % 2) != 0)
        {
            return false;
        }

        var output = new byte[input.Length / 2];
        for (var i = 0; i < output.Length; i++)
        {
            if (!TryParseHexNibble(input[i * 2], out var high) ||
                !TryParseHexNibble(input[(i * 2) + 1], out var low))
            {
                return false;
            }

            output[i] = (byte)((high << 4) | low);
        }

        decoded = output;
        return true;
    }

    private static bool TryParseHexNibble(char c, out int value)
    {
        switch (c)
        {
            case >= '0' and <= '9':
                value = c - '0';
                return true;
            case >= 'a' and <= 'f':
                value = 10 + (c - 'a');
                return true;
            case >= 'A' and <= 'F':
                value = 10 + (c - 'A');
                return true;
            default:
                value = 0;
                return false;
        }
    }

    private static void WriteHexLower(ReadOnlySpan<byte> bytes, Span<char> destination)
    {
        const string Hex = "0123456789abcdef";
        for (var i = 0; i < bytes.Length; i++)
        {
            destination[i * 2] = Hex[bytes[i] >> 4];
            destination[(i * 2) + 1] = Hex[bytes[i] & 0xF];
        }
    }
}
