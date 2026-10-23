// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;

namespace Honua.Core.Features.AuditLog;

/// <summary>
/// One stored audit-chain row, already parsed, ready to replay.
/// </summary>
internal readonly struct AuditChainLink
{
    public AuditChainLink()
    {
    }

    public long AuditId { get; init; }

    public DateTimeOffset Timestamp { get; init; }

    public AuditEventType EventType { get; init; }

    public string Actor { get; init; } = string.Empty;

    public AuditActorType ActorType { get; init; }

    public string ResourceType { get; init; } = string.Empty;

    public string? ResourceId { get; init; }

    public string Action { get; init; } = string.Empty;

    public AuditOutcome Outcome { get; init; }

    public string CorrelationId { get; init; } = string.Empty;

    public string? RemoteIp { get; init; }

    public string? UserAgent { get; init; }

    public string Details { get; init; } = string.Empty;

    public string? PreviousHash { get; init; }

    public string? EntryHash { get; init; }
}

/// <summary>
/// Replays an audit hash chain one row at a time. Leading rows with no hash are the
/// pre-chain prefix. Once a hashed row has been seen, a later row with no hash does
/// not verify. When a chain key is configured, a row matches as a keyed MAC or, only
/// before the first keyed row, as a legacy unkeyed digest. A hashed chain does not
/// verify unless a key is configured and at least one row matches that key.
/// </summary>
internal sealed class AuditChainVerificationCursor
{
    private bool _chainStarted;
    private bool _seenKeyed;
    private string? _expectedPreviousHash;
    private long _rowsChecked;
    private long _unhashedRows;

    /// <summary>
    /// Accepts the next row. Returns a failed report when the row breaks the chain;
    /// otherwise <c>null</c> and the caller continues.
    /// </summary>
    public AuditIntegrityReport? Observe(in AuditChainLink link, ReadOnlyMemory<byte> chainKey)
    {
        var key = chainKey.Span;
        _rowsChecked++;

        var entryHash = NormalizeHash(link.EntryHash);
        var previousHash = NormalizeHash(link.PreviousHash);

        if (entryHash is null)
        {
            if (_chainStarted)
            {
                return Failed(
                    link.AuditId,
                    $"entry_hash is missing at audit_id {link.AuditId}: an unhashed row follows a hashed row.");
            }

            _unhashedRows++;
            return null;
        }

        if (_chainStarted && !string.Equals(previousHash, _expectedPreviousHash, StringComparison.Ordinal))
        {
            return Failed(
                link.AuditId,
                $"prev_hash mismatch at audit_id {link.AuditId}: chain link broken (row deleted or reordered).");
        }

        _chainStarted = true;

        var keyConfigured = key.Length >= AuditChainKeyMaterial.MinimumLength;
        if (keyConfigured && HashesMatch(Mac(key, previousHash, link), entryHash))
        {
            _seenKeyed = true;
        }
        else if (!keyConfigured)
        {
            if (!HashesMatch(Legacy(previousHash, link), entryHash))
            {
                return HashMismatch(link.AuditId);
            }
        }
        else if (_seenKeyed || !HashesMatch(Legacy(previousHash, link), entryHash))
        {
            return HashMismatch(link.AuditId);
        }

        _expectedPreviousHash = entryHash;
        return null;
    }

    /// <summary>Finishes a walk that <see cref="Observe"/> did not already fail.</summary>
    public AuditIntegrityReport Complete(ReadOnlyMemory<byte> chainKey)
    {
        if (_chainStarted && chainKey.Length < AuditChainKeyMaterial.MinimumLength)
        {
            return new AuditIntegrityReport
            {
                Verified = false,
                RowsChecked = _rowsChecked,
                UnhashedRows = _unhashedRows,
                FailureReason = "audit chain key is not configured.",
            };
        }

        if (_chainStarted && !_seenKeyed)
        {
            return new AuditIntegrityReport
            {
                Verified = false,
                RowsChecked = _rowsChecked,
                UnhashedRows = _unhashedRows,
                FailureReason = "audit chain has no keyed row.",
            };
        }

        return new AuditIntegrityReport
        {
            Verified = true,
            RowsChecked = _rowsChecked,
            UnhashedRows = _unhashedRows,
        };
    }

    private AuditIntegrityReport HashMismatch(long auditId)
        => Failed(auditId, $"entry_hash mismatch at audit_id {auditId}: row contents were altered after write.");

    private AuditIntegrityReport Failed(long auditId, string reason)
        => new()
        {
            Verified = false,
            RowsChecked = _rowsChecked,
            UnhashedRows = _unhashedRows,
            FirstBrokenAuditId = auditId,
            FailureReason = reason,
        };

    private static string Mac(ReadOnlySpan<byte> chainKey, string? previousHash, in AuditChainLink link)
        => AuditEntryHasher.ComputeEntryMac(
            chainKey,
            previousHash,
            link.Timestamp,
            link.EventType,
            link.Actor,
            link.ActorType,
            link.ResourceType,
            link.ResourceId,
            link.Action,
            link.Outcome,
            link.CorrelationId,
            link.RemoteIp,
            link.UserAgent,
            link.Details);

    private static string Legacy(string? previousHash, in AuditChainLink link)
        => AuditEntryHasher.ComputeEntryHash(
            previousHash,
            link.Timestamp,
            link.EventType,
            link.Actor,
            link.ActorType,
            link.ResourceType,
            link.ResourceId,
            link.Action,
            link.Outcome,
            link.CorrelationId,
            link.RemoteIp,
            link.UserAgent,
            link.Details);

    private static bool HashesMatch(string computed, string stored)
        => string.Equals(computed, stored, StringComparison.Ordinal);

    private static string? NormalizeHash(string? hash)
    {
        if (string.IsNullOrEmpty(hash))
        {
            return null;
        }

        var trimmed = hash.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
