// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Features.AuditLog;

/// <summary>
/// PostgreSQL implementation of <see cref="IAuditChainGapAttestor"/>. It reads the gap's
/// boundaries from <c>honua.audit_log</c>, refuses anything that is not a single
/// downgrade gap at the verifier's first failure, and appends the declaration through
/// <see cref="IAuditLog"/> so it is chained and keyed like any other row.
/// </summary>
internal sealed class PostgresAuditChainGapAttestor : IAuditChainGapAttestor
{
    private const int MaxReasonLength = 512;

    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;
    private readonly IAuditLog _auditLog;
    private readonly IAuditLogIntegrityVerifier _verifier;
    private readonly string _table;
    private readonly bool _keyConfigured;

    public PostgresAuditChainGapAttestor(
        IAdoNetDatabaseConnectionProvider connectionProvider,
        IAuditLog auditLog,
        IAuditLogIntegrityVerifier verifier,
        string? schemaName,
        ReadOnlyMemory<byte> chainKey)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _table = SchemaSearchPath.QualifyTable("audit_log", schemaName);
        _keyConfigured = chainKey.Length >= AuditChainKeyMaterial.MinimumLength;
    }

    public async Task<AuditChainGapAttestationOutcome> AttestAsync(
        AuditChainGapAttestationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var reason = request.Reason.Trim();
        if (reason.Length == 0 || reason.Length > MaxReasonLength)
        {
            return Refused($"reason is required and must be at most {MaxReasonLength} characters.", report: null);
        }

        // Only a keyed row proves who attested the gap; without a key the verifier
        // could not accept the attestation anyway.
        if (!_keyConfigured)
        {
            return new AuditChainGapAttestationOutcome
            {
                Status = AuditChainGapAttestationStatus.KeyNotConfigured,
                Detail = "AuditLog:ChainVerification:Key is not configured; activate the chain key before attesting a gap.",
            };
        }

        var before = await _verifier.VerifyAsync(cancellationToken).ConfigureAwait(false);
        if (before.FirstBrokenAuditId != request.GapFirstAuditId)
        {
            var actual = before.FirstBrokenAuditId is { } broken
                ? string.Create(CultureInfo.InvariantCulture, $"the first verification failure is at audit_id {broken}")
                : "the chain has no row-level failure";
            return Refused($"gapFirstAuditId must be the first failing row; {actual}.", before);
        }

        var shape = await ReadGapShapeAsync(request.GapFirstAuditId, cancellationToken).ConfigureAwait(false);
        if (shape.Refusal is not null)
        {
            return Refused(shape.Refusal, before);
        }

        var attestation = shape.Attestation! with { Reason = reason };
        var receipt = await _auditLog.RecordAsync(
            new AuditEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                EventType = AuditEventType.AdminAction,
                Actor = request.Actor,
                ActorType = AuditActorType.UserId,
                ResourceType = AuditChainGapAttestation.ResourceType,
                ResourceId = attestation.ResourceId,
                Action = AuditChainGapAttestation.Action,
                Outcome = AuditOutcome.Success,
                CorrelationId = request.CorrelationId,
                RemoteIp = request.RemoteIp,
                UserAgent = request.UserAgent,
                Details = attestation.ToDetails(),
            },
            cancellationToken).ConfigureAwait(false);

        if (receipt is null || !long.TryParse(receipt, NumberStyles.None, CultureInfo.InvariantCulture, out var auditId))
        {
            return new AuditChainGapAttestationOutcome
            {
                Status = AuditChainGapAttestationStatus.WriteFailed,
                Detail = "the audit store did not accept the attestation row.",
                Report = before,
            };
        }

        var after = await _verifier.VerifyAsync(cancellationToken).ConfigureAwait(false);
        return new AuditChainGapAttestationOutcome
        {
            Status = AuditChainGapAttestationStatus.Attested,
            Attestation = attestation with { AttestationAuditId = auditId },
            Report = after,
        };
    }

    private async Task<GapShape> ReadGapShapeAsync(long gapFirst, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionProvider
            .OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);

        var first = await ReadRowAsync(
            connection,
            $"SELECT audit_id, entry_hash, prev_hash FROM {_table} WHERE audit_id = @id",
            gapFirst,
            cancellationToken).ConfigureAwait(false);
        if (first is null || first.Value.EntryHash is not null)
        {
            return GapShape.Refuse("gapFirstAuditId must name an existing row with no entry_hash.");
        }

        var anchor = await ReadRowAsync(
            connection,
            $"SELECT audit_id, entry_hash, prev_hash FROM {_table} WHERE audit_id < @id ORDER BY audit_id DESC LIMIT 1",
            gapFirst,
            cancellationToken).ConfigureAwait(false);
        if (anchor?.EntryHash is not { } anchorHash)
        {
            return GapShape.Refuse("the row before gapFirstAuditId is not hashed, so this is not a downgrade gap.");
        }

        var resume = await ReadRowAsync(
            connection,
            $"SELECT audit_id, entry_hash, prev_hash FROM {_table} WHERE audit_id > @id AND entry_hash IS NOT NULL ORDER BY audit_id ASC LIMIT 1",
            gapFirst,
            cancellationToken).ConfigureAwait(false);
        if (resume is null)
        {
            return GapShape.Refuse("no hashed row follows the gap; deploy a hash-chaining release before attesting it.");
        }

        if (resume.Value.PreviousHash is { } resumePrevious
            && !string.Equals(resumePrevious, anchorHash, StringComparison.Ordinal))
        {
            return GapShape.Refuse(string.Create(
                CultureInfo.InvariantCulture,
                $"audit_id {resume.Value.AuditId} links to neither the anchor nor a new chain; that is not a downgrade gap."));
        }

        await using var command = new NpgsqlCommand(
            $"SELECT MAX(audit_id), COUNT(*) FROM {_table} WHERE audit_id >= @first AND audit_id < @resume",
            connection);
        command.Parameters.AddWithValue("@first", gapFirst);
        command.Parameters.AddWithValue("@resume", resume.Value.AuditId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var gapLast = reader.GetInt64(0);
        var gapRows = reader.GetInt64(1);

        return new GapShape(
            new AuditChainGapAttestation
            {
                AnchorAuditId = anchor.Value.AuditId,
                AnchorEntryHash = anchorHash,
                GapFirstAuditId = gapFirst,
                GapLastAuditId = gapLast,
                GapRowCount = gapRows,
                ResumeAuditId = resume.Value.AuditId,
                ResumePreviousHash = resume.Value.PreviousHash,
                Reason = string.Empty,
            },
            Refusal: null);
    }

    private static async Task<StoredRow?> ReadRowAsync(
        NpgsqlConnection connection,
        string sql,
        long auditId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", auditId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new StoredRow(
            reader.GetInt64(0),
            Normalize(reader.IsDBNull(1) ? null : reader.GetString(1)),
            Normalize(reader.IsDBNull(2) ? null : reader.GetString(2)));
    }

    private static string? Normalize(string? hash)
        => string.IsNullOrWhiteSpace(hash) ? null : hash.Trim();

    private static AuditChainGapAttestationOutcome Refused(string detail, AuditIntegrityReport? report)
        => new()
        {
            Status = AuditChainGapAttestationStatus.Refused,
            Detail = detail,
            Report = report,
        };

    private readonly record struct StoredRow(long AuditId, string? EntryHash, string? PreviousHash);

    private readonly record struct GapShape(AuditChainGapAttestation? Attestation, string? Refusal)
    {
        public static GapShape Refuse(string refusal) => new(null, refusal);
    }
}
