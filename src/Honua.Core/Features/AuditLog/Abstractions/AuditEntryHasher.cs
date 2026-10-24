// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Honua.Core.Features.AuditLog.Abstractions;

/// <summary>
/// Computes the tamper-evident hash chain for audit log rows (#350).
/// </summary>
/// <remarks>
/// <para>
/// Each row carries an <c>entry_hash</c>. New rows use
/// <see cref="ComputeEntryMac"/>, an HMAC-SHA256 under a key that is not stored
/// in the database. Rows written before that key were
/// <c>SHA-256(prev_hash || canonical(row))</c> via <see cref="ComputeEntryHash"/>.
/// <c>canonical(row)</c> is a stable, ordered, separator-escaped serialization of
/// the row's immutable fields, shared by both digests so a verifier can replay
/// either form.
/// </para>
/// <para>
/// The serialization is deliberately framework-free and allocation-light so it
/// is AOT-safe and identical across the writer and the verifier. Field values
/// are length-prefixed to make the encoding injective: no combination of field
/// contents can be confused with a different combination.
/// </para>
/// </remarks>
public static class AuditEntryHasher
{
    /// <summary>
    /// Canonical sentinel written into the chain for a <c>null</c> field value,
    /// distinct from an empty string (which is encoded with length 0).
    /// The leading NUL is part of the stored canonical form; do not drop it or
    /// every existing legacy digest stops matching.
    /// </summary>
    private const string NullSentinel = "\0null";

    /// <summary>
    /// Computes the legacy unkeyed hex SHA-256 <c>entry_hash</c> for an audit row.
    /// </summary>
    /// <param name="previousHash">
    /// The <c>entry_hash</c> of the preceding row, or <c>null</c>/empty for the
    /// genesis row.
    /// </param>
    /// <param name="timestamp">The row timestamp (UTC instant).</param>
    /// <param name="eventType">The event type.</param>
    /// <param name="actor">The actor identifier.</param>
    /// <param name="actorType">The actor classification.</param>
    /// <param name="resourceType">The resource type.</param>
    /// <param name="resourceId">The optional resource identifier.</param>
    /// <param name="action">The dotted action name.</param>
    /// <param name="outcome">The outcome classification.</param>
    /// <param name="correlationId">The correlation identifier.</param>
    /// <param name="remoteIp">The optional remote IP.</param>
    /// <param name="userAgent">The optional user agent.</param>
    /// <param name="details">The opaque detail payload.</param>
    /// <returns>A lowercase 64-character hex SHA-256 digest.</returns>
    public static string ComputeEntryHash(
        string? previousHash,
        DateTimeOffset timestamp,
        AuditEventType eventType,
        string actor,
        AuditActorType actorType,
        string resourceType,
        string? resourceId,
        string action,
        AuditOutcome outcome,
        string correlationId,
        string? remoteIp,
        string? userAgent,
        string details)
    {
        var bytes = Encoding.UTF8.GetBytes(Canonicalize(
            previousHash,
            timestamp,
            eventType,
            actor,
            actorType,
            resourceType,
            resourceId,
            action,
            outcome,
            correlationId,
            remoteIp,
            userAgent,
            details));
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    /// <summary>
    /// Computes the keyed hex HMAC-SHA256 <c>entry_hash</c> for an audit row.
    /// </summary>
    /// <param name="chainKey">Key material held outside the database. At least 32 bytes.</param>
    /// <param name="previousHash">
    /// The <c>entry_hash</c> of the preceding row, or <c>null</c>/empty for the
    /// genesis row.
    /// </param>
    /// <param name="timestamp">The row timestamp (UTC instant).</param>
    /// <param name="eventType">The event type.</param>
    /// <param name="actor">The actor identifier.</param>
    /// <param name="actorType">The actor classification.</param>
    /// <param name="resourceType">The resource type.</param>
    /// <param name="resourceId">The optional resource identifier.</param>
    /// <param name="action">The dotted action name.</param>
    /// <param name="outcome">The outcome classification.</param>
    /// <param name="correlationId">The correlation identifier.</param>
    /// <param name="remoteIp">The optional remote IP.</param>
    /// <param name="userAgent">The optional user agent.</param>
    /// <param name="details">The opaque detail payload.</param>
    /// <returns>A lowercase 64-character hex HMAC-SHA256 digest.</returns>
    public static string ComputeEntryMac(
        ReadOnlySpan<byte> chainKey,
        string? previousHash,
        DateTimeOffset timestamp,
        AuditEventType eventType,
        string actor,
        AuditActorType actorType,
        string resourceType,
        string? resourceId,
        string action,
        AuditOutcome outcome,
        string correlationId,
        string? remoteIp,
        string? userAgent,
        string details)
    {
        if (chainKey.Length < AuditChainKeyMaterial.MinimumLength)
        {
            throw new ArgumentException("The audit chain key must be at least 32 bytes.", nameof(chainKey));
        }

        var bytes = Encoding.UTF8.GetBytes(Canonicalize(
            previousHash,
            timestamp,
            eventType,
            actor,
            actorType,
            resourceType,
            resourceId,
            action,
            outcome,
            correlationId,
            remoteIp,
            userAgent,
            details));
        return Convert.ToHexStringLower(HMACSHA256.HashData(chainKey, bytes));
    }

    private static string Canonicalize(
        string? previousHash,
        DateTimeOffset timestamp,
        AuditEventType eventType,
        string actor,
        AuditActorType actorType,
        string resourceType,
        string? resourceId,
        string action,
        AuditOutcome outcome,
        string correlationId,
        string? remoteIp,
        string? userAgent,
        string details)
    {
        var builder = new StringBuilder(256);

        // Chain link first so the genesis row (no prev) is distinguishable.
        AppendField(builder, string.IsNullOrEmpty(previousHash) ? null : previousHash);
        // Encode the timestamp at microsecond precision so the writer (hashing the
        // in-memory value) and the verifier (hashing the value read back from a
        // PostgreSQL TIMESTAMPTZ, which stores microseconds) agree. 1 microsecond
        // == 10 .NET ticks; truncating here makes the chain round-trip through the
        // database lossless.
        var microseconds = timestamp.UtcTicks / TimeSpan.TicksPerMicrosecond;
        AppendField(builder, microseconds.ToString(CultureInfo.InvariantCulture));
        AppendField(builder, eventType.ToString());
        AppendField(builder, actor);
        AppendField(builder, actorType.ToString());
        AppendField(builder, resourceType);
        AppendField(builder, resourceId);
        AppendField(builder, action);
        AppendField(builder, outcome.ToString());
        AppendField(builder, correlationId);
        AppendField(builder, remoteIp);
        AppendField(builder, userAgent);
        AppendField(builder, details ?? string.Empty);
        return builder.ToString();
    }

    private static void AppendField(StringBuilder builder, string? value)
    {
        if (value is null)
        {
            builder.Append(NullSentinel).Append('|');
            return;
        }

        // Length-prefix makes the concatenation injective so distinct field
        // tuples can never collide into the same canonical string.
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append('|');
    }
}
