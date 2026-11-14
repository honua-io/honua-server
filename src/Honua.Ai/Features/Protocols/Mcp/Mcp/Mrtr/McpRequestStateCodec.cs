// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Honua.Ai.Protocols.Mcp.Mrtr;

internal enum McpRequestStateKind : byte
{
    Clarification = 1,

    Approval = 2
}

// Who may continue a call: the authenticated principal, the tool and the arguments it was called with.
internal sealed record McpRequestStateBinding(string PrincipalKey, string ToolName, JsonElement? Arguments);

// What the server wants to resume, and until when.
internal sealed record McpRequestStateGrant(McpRequestStateKind Kind, string Reference, DateTimeOffset ExpiresUtc);

// A verified requestState. The reference is an identifier the server re-reads from its own store;
// it is never treated as evidence of a decision.
internal sealed record McpRequestState(McpRequestStateKind Kind, string Reference, DateTimeOffset IssuedUtc);

// MRTR requestState (SEP-2322). The client is untrusted, so the state is tagged, bound to the
// authenticated principal, the tool and its arguments, and expires. It holds identifiers only;
// every replica derives the same keys from configuration and keeps no memory.
internal sealed class McpRequestStateCodec
{
    internal const string Prefix = "hr1.";

    internal const int MaxEncodedLength = 1024;

    internal const int MaxReferenceBytes = 640;

    internal static readonly TimeSpan ClarificationLifetime = TimeSpan.FromMinutes(15);

    private const byte FormatVersion = 1;
    private const int KindOffset = 1;
    private const int IssuedOffset = 2;
    private const int ExpiresOffset = IssuedOffset + 8;
    private const int NonceOffset = ExpiresOffset + 8;
    private const int NonceLength = 16;
    private const int BindingOffset = NonceOffset + NonceLength;
    private const int BindingLength = 16;
    private const int ContextOffset = BindingOffset + BindingLength;
    private const int ContextLength = 32;
    private const int ReferenceLengthOffset = ContextOffset + ContextLength;
    private const int HeaderLength = ReferenceLengthOffset + 2;
    private const int TagLength = 32;

    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);
    private static readonly byte[] KeyDerivationSalt = Encoding.UTF8.GetBytes("Honua.Server");
    private static readonly byte[] TagKeyInfo = Encoding.UTF8.GetBytes("honua.mcp.request-state.v1/tag");
    private static readonly byte[] BindingKeyInfo = Encoding.UTF8.GetBytes("honua.mcp.request-state.v1/principal");
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly byte[] _tagKey;
    private readonly byte[] _bindingKey;
    private readonly TimeProvider _timeProvider;

    public McpRequestStateCodec(string secret, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        var ikm = Encoding.UTF8.GetBytes(secret);
        _tagKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, KeyDerivationSalt, TagKeyInfo);
        _bindingKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, KeyDerivationSalt, BindingKeyInfo);
        CryptographicOperations.ZeroMemory(ikm);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public DateTimeOffset UtcNow => _timeProvider.GetUtcNow();

    public static bool FitsReference(string reference) => Encoding.UTF8.GetByteCount(reference) <= MaxReferenceBytes;

    public string Mint(McpRequestStateBinding binding, McpRequestStateGrant grant)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentException.ThrowIfNullOrEmpty(grant.Reference);
        var referenceBytes = Encoding.UTF8.GetBytes(grant.Reference);
        if (referenceBytes.Length > MaxReferenceBytes)
        {
            throw new ArgumentException($"A requestState reference is limited to {MaxReferenceBytes} bytes.", nameof(grant));
        }

        var now = _timeProvider.GetUtcNow();
        if (grant.ExpiresUtc <= now)
        {
            throw new ArgumentException("A requestState must expire in the future.", nameof(grant));
        }

        var buffer = new byte[HeaderLength + referenceBytes.Length + TagLength];
        var span = buffer.AsSpan();
        span[0] = FormatVersion;
        span[KindOffset] = (byte)grant.Kind;
        BinaryPrimitives.WriteInt64BigEndian(span.Slice(IssuedOffset, 8), now.ToUnixTimeSeconds());
        BinaryPrimitives.WriteInt64BigEndian(span.Slice(ExpiresOffset, 8), grant.ExpiresUtc.ToUnixTimeSeconds());
        RandomNumberGenerator.Fill(span.Slice(NonceOffset, NonceLength));
        WriteBinding(binding.PrincipalKey, span.Slice(BindingOffset, BindingLength));
        WriteContext(binding, span.Slice(ContextOffset, ContextLength));
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(ReferenceLengthOffset, 2), (ushort)referenceBytes.Length);
        referenceBytes.CopyTo(span[HeaderLength..]);
        var payloadLength = HeaderLength + referenceBytes.Length;
        WriteTag(span[..payloadLength], span.Slice(payloadLength, TagLength));
        return Prefix + Base64Url.EncodeToString(buffer);
    }

    // Every check must pass; a refused state never falls back to a fresh execution.
    public McpRequestState? Read(string? requestState, McpRequestStateBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (Decode(requestState) is not { } buffer || !HasValidTag(buffer))
        {
            return null;
        }

        var span = buffer.AsSpan();
        var kind = (McpRequestStateKind)span[KindOffset];
        if (kind is not (McpRequestStateKind.Clarification or McpRequestStateKind.Approval)
            || ReadIssued(span) is not { } issued
            || !IsBound(span, binding))
        {
            return null;
        }

        return ReadReference(span) is { } reference ? new McpRequestState(kind, reference, issued) : null;
    }

    // Only the canonical encoding of a well-formed buffer is accepted, so one state has one spelling.
    private static byte[]? Decode(string? requestState)
    {
        if (string.IsNullOrEmpty(requestState)
            || requestState.Length > MaxEncodedLength
            || !requestState.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        byte[] buffer;
        try
        {
            buffer = Base64Url.DecodeFromChars(requestState.AsSpan(Prefix.Length));
        }
        catch (FormatException)
        {
            return null;
        }

        if (buffer.Length < HeaderLength + TagLength
            || buffer[0] != FormatVersion
            || !requestState.AsSpan(Prefix.Length).SequenceEqual(Base64Url.EncodeToString(buffer)))
        {
            return null;
        }

        int referenceLength = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(ReferenceLengthOffset, 2));
        return referenceLength is > 0 and <= MaxReferenceBytes && buffer.Length == HeaderLength + referenceLength + TagLength
            ? buffer
            : null;
    }

    private bool HasValidTag(byte[] buffer)
    {
        var payloadLength = buffer.Length - TagLength;
        Span<byte> expected = stackalloc byte[TagLength];
        WriteTag(buffer.AsSpan(0, payloadLength), expected);
        return CryptographicOperations.FixedTimeEquals(expected, buffer.AsSpan(payloadLength, TagLength));
    }

    private DateTimeOffset? ReadIssued(ReadOnlySpan<byte> span)
    {
        var issuedSeconds = BinaryPrimitives.ReadInt64BigEndian(span.Slice(IssuedOffset, 8));
        var expiresSeconds = BinaryPrimitives.ReadInt64BigEndian(span.Slice(ExpiresOffset, 8));
        var maxSeconds = DateTimeOffset.MaxValue.ToUnixTimeSeconds();
        if (issuedSeconds < 0 || expiresSeconds < 0 || issuedSeconds > maxSeconds || expiresSeconds > maxSeconds)
        {
            return null;
        }

        var issued = DateTimeOffset.FromUnixTimeSeconds(issuedSeconds);
        var now = _timeProvider.GetUtcNow();
        return issued > now + ClockSkew || now >= DateTimeOffset.FromUnixTimeSeconds(expiresSeconds) ? null : issued;
    }

    private bool IsBound(ReadOnlySpan<byte> span, McpRequestStateBinding binding)
    {
        Span<byte> principal = stackalloc byte[BindingLength];
        WriteBinding(binding.PrincipalKey, principal);
        Span<byte> context = stackalloc byte[ContextLength];
        WriteContext(binding, context);
        return CryptographicOperations.FixedTimeEquals(principal, span.Slice(BindingOffset, BindingLength))
            & CryptographicOperations.FixedTimeEquals(context, span.Slice(ContextOffset, ContextLength));
    }

    private static string? ReadReference(ReadOnlySpan<byte> span)
    {
        int referenceLength = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(ReferenceLengthOffset, 2));
        try
        {
            return StrictUtf8.GetString(span.Slice(HeaderLength, referenceLength));
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
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

    // The retry repeats the method and arguments, but not necessarily byte for byte, so the binding
    // hashes a canonical form: object keys in ordinal order, no insignificant whitespace. Number text
    // is kept as sent, so a client must not reformat numbers between the two calls.
    private static void WriteContext(McpRequestStateBinding binding, Span<byte> destination)
    {
        using var buffer = new MemoryStream();
        buffer.Write(Encoding.UTF8.GetBytes(binding.ToolName));
        buffer.WriteByte(0);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            if (binding.Arguments is { ValueKind: not JsonValueKind.Undefined } value)
            {
                WriteCanonical(writer, value);
            }
            else
            {
                writer.WriteNullValue();
            }
        }

        SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), destination);
    }

    internal static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(static p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
