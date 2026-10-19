// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Honua.Ai.Protocols.Mcp;

/// <summary>
/// How MCP Streamable-HTTP sessions are kept, reported on the <c>initialize</c>
/// result as <c>_meta["honua.io/session-mode"]</c> so a client or certification
/// harness can record which topology answered it.
/// </summary>
internal enum McpSessionMode
{
    /// <summary>
    /// Sessions live in this process only. Correct for a single instance; another
    /// instance answers the id with HTTP 404.
    /// </summary>
    InMemory,

    /// <summary>
    /// Sessions are shared through <see cref="IMcpSessionStore"/> (Redis): the
    /// sliding idle expiry and <c>DELETE /mcp</c> apply on every instance.
    /// </summary>
    Shared,

    /// <summary>
    /// No shared store on a host that runs as several instances (AWS Lambda,
    /// <c>Deployment:Mode=MultiNode</c>): the session id is a signed token that
    /// carries the negotiated state, so any instance can serve it without memory
    /// of the instance that minted it.
    /// </summary>
    Stateless
}

/// <summary>
/// Configured selection for <see cref="McpSessionMode"/> (<c>Mcp:SessionMode</c>).
/// A composed shared store always wins.
/// </summary>
internal enum McpSessionModeSetting
{
    /// <summary>
    /// Shared with a store; otherwise stateless on a multi-instance host and
    /// in-memory on a single instance.
    /// </summary>
    Auto,

    /// <summary>Keep sessions in process memory even on a multi-instance host.</summary>
    InMemory,

    /// <summary>Use signed stateless session ids even on a single instance.</summary>
    Stateless
}

/// <summary>
/// The negotiated session state carried inside a stateless session id.
/// </summary>
/// <param name="ElicitationSupported">Whether the client advertised elicitation at <c>initialize</c>.</param>
/// <param name="WorkflowView">The workflow discovery view negotiated at <c>initialize</c>, or <c>null</c>.</param>
/// <param name="CreatedUtc">When the session was minted (whole seconds).</param>
internal sealed record McpStatelessSessionClaims(
    bool ElicitationSupported,
    string? WorkflowView,
    DateTimeOffset CreatedUtc);

/// <summary>
/// Mints and validates self-describing MCP session ids for
/// <see cref="McpSessionMode.Stateless"/>. A token carries the negotiated
/// workflow view, the elicitation flag, the mint time, a random nonce, and a
/// keyed binding of the principal that ran <c>initialize</c>, followed by a
/// truncated HMAC-SHA256 tag. Every instance derives the same key from the same
/// deployment secret, so a session minted on one instance is honoured on any
/// other with no shared memory. An id that fails the tag check, has expired, or
/// was minted under another key answers HTTP 404 as before.
/// </summary>
/// <remarks>
/// The id confers no authority: every request is still authenticated and every
/// <c>tools/call</c> reauthorized, and the principal binding means only the
/// principal that ran <c>initialize</c> can present it. A stateless session
/// cannot slide an idle window or be revoked across instances, so it is bounded
/// by an absolute lifetime instead (<see cref="McpOptions.StatelessSessionLifetime"/>).
/// The principal key itself is never written into the id, only a keyed hash of it.
/// </remarks>
internal sealed class McpStatelessSessionTokens
{
    /// <summary>Prefix that marks a stateless session id (visible ASCII, per the transport).</summary>
    internal const string Prefix = "hs1.";

    /// <summary>Longest workflow view name, in UTF-8 bytes, a token can carry.</summary>
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

    /// <summary>Tolerated clock skew between instances for a token's mint time.</summary>
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    private static readonly byte[] KeyDerivationSalt = Encoding.UTF8.GetBytes("Honua.Server");
    private static readonly byte[] TagKeyInfo = Encoding.UTF8.GetBytes("honua.mcp.session-token.v1/tag");
    private static readonly byte[] BindingKeyInfo = Encoding.UTF8.GetBytes("honua.mcp.session-token.v1/principal");
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly byte[] _tagKey;
    private readonly byte[] _bindingKey;
    private readonly TimeSpan _lifetime;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates a token codec keyed from <paramref name="secret"/>, a value every
    /// instance of the deployment shares (<c>Mcp:SessionSigningKey</c>, or the
    /// database connection string). The secret is only used as HKDF input.
    /// </summary>
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

    /// <summary>The absolute lifetime of a stateless session.</summary>
    public TimeSpan Lifetime => _lifetime;

    /// <summary>
    /// Mints a session id carrying the negotiated state, bound to
    /// <paramref name="principalKey"/>.
    /// </summary>
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

    /// <summary>
    /// Validates a presented id: format, tag, and lifetime. Returns <c>false</c>
    /// for anything that was not minted under this key or has expired. The
    /// principal binding is returned for <see cref="IsBoundTo"/>.
    /// </summary>
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

    /// <summary>
    /// Whether <paramref name="principalBinding"/>, read from a token, names
    /// <paramref name="principalKey"/>. Constant time.
    /// </summary>
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
