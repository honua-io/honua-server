// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Honua.Ai.Protocols.Mcp;

internal enum McpSessionMode
{
    InMemory,

    Shared,

    Stateless
}

internal enum McpSessionModeSetting
{
    Auto,

    InMemory,

    Stateless
}

internal sealed record McpStatelessSessionClaims(
    bool ElicitationSupported,
    string? WorkflowView,
    DateTimeOffset CreatedUtc);

// The session id carries a keyed hash of the principal, never the principal key, and confers no authority by itself.
internal sealed class McpStatelessSessionTokens
{
    internal const string Prefix = "hs1.";

    internal const int MaxViewBytes = 64;

    private const byte FormatVersion = 1;
    private const byte ElicitationFlag = 0x01;
    private const int CreatedOffset = 2;
    private const int NonceOffset = CreatedOffset + 8;
    private const int NonceLength = 16;
    private const int BindingOffset = NonceOffset + NonceLength;
    private const int BindingLength = 16;
    private const int ViewLengthOffset = BindingOffset + BindingLength;
    private const int HeaderLength = ViewLengthOffset + 1;
    private const int TagLength = 16;
    private const int MaxEncodedLength = 256;

    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    private static readonly byte[] KeyDerivationSalt = Encoding.UTF8.GetBytes("Honua.Server");
    private static readonly byte[] TagKeyInfo = Encoding.UTF8.GetBytes("honua.mcp.session-token.v1/tag");
    private static readonly byte[] BindingKeyInfo = Encoding.UTF8.GetBytes("honua.mcp.session-token.v1/principal");
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly byte[] _tagKey;
    private readonly byte[] _bindingKey;
    private readonly TimeSpan _lifetime;
    private readonly TimeProvider _timeProvider;

    public McpStatelessSessionTokens(string secret, TimeSpan lifetime, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        var ikm = Encoding.UTF8.GetBytes(secret);
        _tagKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, KeyDerivationSalt, TagKeyInfo);
        _bindingKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, KeyDerivationSalt, BindingKeyInfo);
        CryptographicOperations.ZeroMemory(ikm);
        _lifetime = lifetime > TimeSpan.Zero ? lifetime : McpOptions.DefaultStatelessSessionLifetime;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public TimeSpan Lifetime => _lifetime;

    public string Mint(string principalKey, bool elicitationSupported, string? workflowView)
    {
        ArgumentNullException.ThrowIfNull(principalKey);
        var view = workflowView is null ? [] : Encoding.UTF8.GetBytes(workflowView);
        if (view.Length > MaxViewBytes)
        {
            throw new ArgumentException(
                $"A workflow view name longer than {MaxViewBytes} bytes cannot be carried in a session id.",
                nameof(workflowView));
        }

        var buffer = new byte[HeaderLength + view.Length + TagLength];
        var span = buffer.AsSpan();
        span[0] = FormatVersion;
        span[1] = elicitationSupported ? ElicitationFlag : (byte)0;
        BinaryPrimitives.WriteInt64BigEndian(span.Slice(CreatedOffset, 8), _timeProvider.GetUtcNow().ToUnixTimeSeconds());
        RandomNumberGenerator.Fill(span.Slice(NonceOffset, NonceLength));
        WriteBinding(principalKey, span.Slice(BindingOffset, BindingLength));
        span[ViewLengthOffset] = (byte)view.Length;
        view.CopyTo(span[HeaderLength..]);
        var payloadLength = HeaderLength + view.Length;
        WriteTag(span[..payloadLength], span.Slice(payloadLength, TagLength));
        return Prefix + Base64Url.EncodeToString(buffer);
    }

    public bool TryRead(string sessionId, out McpStatelessSessionClaims claims, out byte[] principalBinding)
    {
        claims = null!;
        principalBinding = [];
        if (string.IsNullOrEmpty(sessionId)
            || sessionId.Length > MaxEncodedLength
            || !sessionId.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        byte[] buffer;
        try
        {
            buffer = Base64Url.DecodeFromChars(sessionId.AsSpan(Prefix.Length));
        }
        catch (FormatException)
        {
            return false;
        }

        // Only the canonical encoding is accepted, so one session has exactly one id.
        if (buffer.Length < HeaderLength + TagLength
            || buffer[0] != FormatVersion
            || !sessionId.AsSpan(Prefix.Length).SequenceEqual(Base64Url.EncodeToString(buffer)))
        {
            return false;
        }

        var span = buffer.AsSpan();
        int viewLength = span[ViewLengthOffset];
        if (viewLength > MaxViewBytes || buffer.Length != HeaderLength + viewLength + TagLength)
        {
            return false;
        }

        var payloadLength = HeaderLength + viewLength;
        Span<byte> expected = stackalloc byte[TagLength];
        WriteTag(span[..payloadLength], expected);
        if (!CryptographicOperations.FixedTimeEquals(expected, span.Slice(payloadLength, TagLength)))
        {
            return false;
        }

        var seconds = BinaryPrimitives.ReadInt64BigEndian(span.Slice(CreatedOffset, 8));
        if (seconds < 0 || seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            return false;
        }

        var created = DateTimeOffset.FromUnixTimeSeconds(seconds);
        var now = _timeProvider.GetUtcNow();
        if (created > now + ClockSkew || now - created > _lifetime)
        {
            return false;
        }

        string? view;
        try
        {
            view = viewLength == 0 ? null : StrictUtf8.GetString(span.Slice(HeaderLength, viewLength));
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        claims = new McpStatelessSessionClaims((span[1] & ElicitationFlag) != 0, view, created);
        principalBinding = span.Slice(BindingOffset, BindingLength).ToArray();
        return true;
    }

    public bool IsBoundTo(ReadOnlySpan<byte> principalBinding, string principalKey)
    {
        Span<byte> expected = stackalloc byte[BindingLength];
        WriteBinding(principalKey, expected);
        return CryptographicOperations.FixedTimeEquals(expected, principalBinding);
    }

    private void WriteBinding(string principalKey, Span<byte> destination)
    {
        Span<byte> hash = stackalloc byte[32];
        HMACSHA256.HashData(_bindingKey, Encoding.UTF8.GetBytes(principalKey), hash);
        hash[..destination.Length].CopyTo(destination);
    }

    private void WriteTag(ReadOnlySpan<byte> payload, Span<byte> destination)
    {
        Span<byte> hash = stackalloc byte[32];
        HMACSHA256.HashData(_tagKey, payload, hash);
        hash[..destination.Length].CopyTo(destination);
    }
}
