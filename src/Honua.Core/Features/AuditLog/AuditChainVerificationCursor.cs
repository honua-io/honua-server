// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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
/// not verify, unless an <see cref="AuditChainGapAttestation"/> declares exactly that
/// run of rows (see <see cref="AuditChainVerificationCursor(IReadOnlyList{AuditChainGapAttestation})"/>).
/// When a chain key is configured, a row matches as a keyed MAC or, only before the
/// first keyed row, as a legacy unkeyed digest. A hashed chain does not verify unless
/// a key is configured and at least one row matches that key.
/// </summary>
internal sealed class AuditChainVerificationCursor
{
    private readonly IReadOnlyList<AuditChainGapAttestation> _attestations;
    private readonly HashSet<long> _attestationsToConfirm = [];
    private bool _chainStarted;
    private bool _seenKeyed;
    private string? _expectedPreviousHash;
    private long _lastHashedAuditId;
    private long _rowsChecked;
    private long _unhashedRows;
    private long _attestedGapRows;
    private AuditChainGapAttestation? _openGap;
    private long _openGapRows;

    /// <summary>Creates a cursor that accepts no unhashed gap after the chain starts.</summary>
    public AuditChainVerificationCursor()
        : this([])
    {
    }

    /// <summary>
    /// Creates a cursor that accepts a run of unhashed rows after the chain started only
    /// when one of <paramref name="attestations"/> declares it exactly: the gap starts
    /// right after the anchor row whose stored <c>entry_hash</c> it names, contains only
    /// unhashed rows and exactly the declared count, and is followed by the declared
    /// resume row with the declared <c>prev_hash</c>. The attestation row itself must
    /// then verify as a keyed MAC, so only a holder of the chain key can excuse a gap.
    /// </summary>
    /// <param name="attestations">Parsed attestation rows, in <c>audit_id</c> order.</param>
    public AuditChainVerificationCursor(IReadOnlyList<AuditChainGapAttestation> attestations)
    {
        _attestations = attestations ?? [];
    }

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
                return ObserveGapRow(link.AuditId);
            }

            _unhashedRows++;
            return null;
        }

        if (_openGap is { } gap)
        {
            var resumeFailure = CloseGap(gap, link.AuditId, previousHash);
            if (resumeFailure is not null)
            {
                return resumeFailure;
            }
        }
        else if (_chainStarted && !string.Equals(previousHash, _expectedPreviousHash, StringComparison.Ordinal))
        {
            return Failed(
                link.AuditId,
                $"prev_hash mismatch at audit_id {link.AuditId}: chain link broken (row deleted or reordered).");
        }

        _chainStarted = true;

        var keyConfigured = key.Length >= AuditChainKeyMaterial.MinimumLength;
        var matchedKeyed = keyConfigured && HashesMatch(Mac(key, previousHash, link), entryHash);
        if (_attestationsToConfirm.Contains(link.AuditId) && !matchedKeyed)
        {
            return Failed(
                link.AuditId,
                $"gap attestation at audit_id {link.AuditId} is not a keyed row: only the chain key holder can attest a gap.");
        }

        if (matchedKeyed)
        {
            _seenKeyed = true;
            _attestationsToConfirm.Remove(link.AuditId);
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
        _lastHashedAuditId = link.AuditId;
        return null;
    }

    /// <summary>Finishes a walk that <see cref="Observe"/> did not already fail.</summary>
    public AuditIntegrityReport Complete(ReadOnlyMemory<byte> chainKey)
    {
        if (_openGap is { } openGap)
        {
            return Failed(
                openGap.GapFirstAuditId,
                $"entry_hash is missing at audit_id {openGap.GapFirstAuditId}: the attested gap never resumes.");
        }

        if (_attestationsToConfirm.Count > 0)
        {
            var missing = _attestationsToConfirm.Min();
            return Failed(missing, $"gap attestation at audit_id {missing} was not found in the chain.");
        }

        if (_chainStarted && chainKey.Length < AuditChainKeyMaterial.MinimumLength)
        {
            return new AuditIntegrityReport
            {
                Verified = false,
                RowsChecked = _rowsChecked,
                UnhashedRows = _unhashedRows,
                AttestedGapRows = _attestedGapRows,
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
                AttestedGapRows = _attestedGapRows,
                FailureReason = "audit chain has no keyed row.",
            };
        }

        return new AuditIntegrityReport
        {
            Verified = true,
            RowsChecked = _rowsChecked,
            UnhashedRows = _unhashedRows,
            AttestedGapRows = _attestedGapRows,
        };
    }

    private AuditIntegrityReport? ObserveGapRow(long auditId)
    {
        if (_openGap is { } gap && auditId <= gap.GapLastAuditId)
        {
            _openGapRows++;
            return null;
        }

        if (_openGap is null)
        {
            foreach (var candidate in _attestations)
            {
                if (candidate.GapFirstAuditId == auditId
                    && candidate.AnchorAuditId == _lastHashedAuditId
                    && string.Equals(candidate.AnchorEntryHash, _expectedPreviousHash, StringComparison.Ordinal))
                {
                    _openGap = candidate;
                    _openGapRows = 1;
                    return null;
                }
            }
        }

        return Failed(
            auditId,
            $"entry_hash is missing at audit_id {auditId}: an unhashed row follows a hashed row.");
    }

    private AuditIntegrityReport? CloseGap(AuditChainGapAttestation gap, long auditId, string? previousHash)
    {
        if (auditId <= gap.GapLastAuditId)
        {
            return Failed(
                auditId,
                $"hashed row at audit_id {auditId} lies inside the gap attested at audit_id {gap.AttestationAuditId}.");
        }

        if (auditId != gap.ResumeAuditId
            || _openGapRows != gap.GapRowCount
            || !string.Equals(previousHash, gap.ResumePreviousHash, StringComparison.Ordinal))
        {
            return Failed(
                auditId,
                $"audit_id {auditId} does not resume the chain as attested at audit_id {gap.AttestationAuditId}.");
        }

        _attestedGapRows += _openGapRows;
        _attestationsToConfirm.Add(gap.AttestationAuditId);
        _openGap = null;
        _openGapRows = 0;
        return null;
    }

    private AuditIntegrityReport HashMismatch(long auditId)
        => Failed(auditId, $"entry_hash mismatch at audit_id {auditId}: row contents were altered after write.");

    private AuditIntegrityReport Failed(long auditId, string reason)
        => new()
        {
            Verified = false,
            RowsChecked = _rowsChecked,
            UnhashedRows = _unhashedRows,
            AttestedGapRows = _attestedGapRows,
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
