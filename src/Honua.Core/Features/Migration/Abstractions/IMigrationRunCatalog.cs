// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.Migration.Abstractions;

/// <summary>
/// Slice 5 of issue #1015. Storage abstraction for
/// <see cref="MigrationRunRecord"/> rows that back the admin orchestration
/// endpoints (list runs, get run, fetch evidence pack, cancel run).
/// </summary>
/// <remarks>
/// <para>
/// Implementations must be idempotent for repeat <see cref="RecordStartedAsync"/>
/// and <see cref="RecordCompletedAsync"/> calls with the same run id so apply
/// services can retry safely without producing duplicate rows.
/// </para>
/// <para>
/// The catalog does not own the evidence pack body. The evidence pack JSON is
/// supplied to <see cref="RecordCompletedAsync"/> and returned verbatim by
/// <see cref="GetEvidencePackAsync"/>. Implementations may store the body as a
/// column or as a reference to an external object store, but the abstraction
/// keeps the surface a single byte payload so the admin endpoints can stream
/// it back without re-deriving anything.
/// </para>
/// </remarks>
public interface IMigrationRunCatalog
{
    /// <summary>
    /// Record the start of a migration run. Idempotent: re-recording a
    /// run-id that already exists in any status leaves the existing row
    /// untouched and returns it.
    /// </summary>
    /// <param name="record">Initial record. Status should be
    /// <see cref="MigrationRunStatus.Running"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<MigrationRunRecord> RecordStartedAsync(
        MigrationRunRecord record,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Record the terminal state of a previously-started run. Updates the
    /// row's status, completed-at, evidence-pack reference/fingerprint, and
    /// status note. Returns the persisted record. If the run is already
    /// terminal (succeeded, failed, or cancelled), the existing row is
    /// returned without overwrite — terminal state is sticky so cancel/
    /// failure cannot be silently overwritten by a late success notification.
    /// </summary>
    /// <param name="runId">Run identifier to update.</param>
    /// <param name="status">Terminal status (Succeeded, Failed, Cancelled).</param>
    /// <param name="completedAt">Completion timestamp.</param>
    /// <param name="evidencePackRef">Optional storage reference for the
    /// emitted evidence pack.</param>
    /// <param name="evidencePackFingerprint">Optional bundle fingerprint
    /// from the emitted evidence pack.</param>
    /// <param name="evidencePackBody">Optional evidence pack JSON body to
    /// persist alongside the row. Implementations are free to store this
    /// inline or externalize it.</param>
    /// <param name="statusNote">Optional operator-visible note.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<MigrationRunRecord?> RecordCompletedAsync(
        string runId,
        MigrationRunStatus status,
        DateTimeOffset completedAt,
        string? evidencePackRef,
        string? evidencePackFingerprint,
        string? evidencePackBody,
        string? statusNote,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Mark a running run as cancelled. Returns the updated record, or null
    /// if the run-id is unknown. If the run is already terminal, the
    /// existing row is returned without overwrite.
    /// </summary>
    /// <param name="runId">Run identifier to cancel.</param>
    /// <param name="cancelledAt">Cancellation timestamp.</param>
    /// <param name="statusNote">Optional operator-visible note.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<MigrationRunRecord?> CancelAsync(
        string runId,
        DateTimeOffset cancelledAt,
        string? statusNote,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetch a single run by id, or null if unknown.
    /// </summary>
    Task<MigrationRunRecord?> GetAsync(
        string runId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Return a paged window of runs ordered by <c>started_at DESC</c>.
    /// </summary>
    /// <param name="query">Pagination + optional filter parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<MigrationRunListPage> ListAsync(
        MigrationRunListQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Return the evidence-pack JSON body recorded for the given run, or
    /// null if the run is unknown or has no evidence pack persisted yet.
    /// </summary>
    Task<string?> GetEvidencePackAsync(
        string runId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Record the signed reconciliation scorecard (issue #1381) for an existing run. Persists the
    /// scorecard fingerprint on the run row and the scorecard JSON body for later retrieval via
    /// <see cref="GetScorecardAsync"/>. Returns the updated record, or <c>null</c> if the run-id is
    /// unknown. Recording is independent of <see cref="RecordCompletedAsync"/> so a scorecard can be
    /// attached whether the run completed or was routed to review.
    /// </summary>
    /// <param name="runId">Run identifier to attach the scorecard to.</param>
    /// <param name="scorecardFingerprint">Signed scorecard fingerprint (<c>sha256:&lt;hex&gt;</c>).</param>
    /// <param name="scorecardBody">Scorecard JSON body to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<MigrationRunRecord?> RecordScorecardAsync(
        string runId,
        string scorecardFingerprint,
        string scorecardBody,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Return the reconciliation-scorecard JSON body recorded for the given run, or <c>null</c> if
    /// the run is unknown or no scorecard has been recorded yet.
    /// </summary>
    Task<string?> GetScorecardAsync(
        string runId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Pagination + filter parameters for
/// <see cref="IMigrationRunCatalog.ListAsync"/>.
/// </summary>
public sealed record MigrationRunListQuery
{
    /// <summary>
    /// Maximum number of records to return. Clamped by the implementation
    /// to a safe upper bound (typically 100).
    /// </summary>
    public int Limit { get; init; } = 25;

    /// <summary>
    /// Number of records to skip (offset-based paging).
    /// </summary>
    public int Offset { get; init; }

    /// <summary>
    /// Optional source-kind filter (case-insensitive exact match).
    /// </summary>
    public string? SourceKind { get; init; }

    /// <summary>
    /// Optional status filter.
    /// </summary>
    public MigrationRunStatus? Status { get; init; }
}

/// <summary>
/// One page of <see cref="MigrationRunRecord"/> results.
/// </summary>
public sealed record MigrationRunListPage
{
    /// <summary>
    /// Records in this page, ordered most-recent first.
    /// </summary>
    public required IReadOnlyList<MigrationRunRecord> Items { get; init; }

    /// <summary>
    /// Total count matching the query (before paging). Used by the admin
    /// endpoints to render "page X of N" UI without an extra round trip.
    /// </summary>
    public required long TotalCount { get; init; }

    /// <summary>
    /// Echo of the limit applied (after clamping).
    /// </summary>
    public required int Limit { get; init; }

    /// <summary>
    /// Echo of the offset applied.
    /// </summary>
    public required int Offset { get; init; }
}
