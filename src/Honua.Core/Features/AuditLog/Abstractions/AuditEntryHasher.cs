// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Honua.Core.Features.AuditLog.Abstractions;

// New rows are HMAC-SHA256 under a key that is not stored in the database; older rows stay SHA-256 of the previous hash and the canonical row.
public static class AuditEntryHasher
{
    // A null field uses a sentinel distinct from an empty string, and dropping that sentinel would invalidate every existing legacy digest.
    private const string NullSentinel = "\0null";

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
