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
using Honua.Core.Features.FileImport.Services.FileGdb;

namespace Honua.Core.Features.Migration.Services;

/// <summary>
/// Abstraction for persisting and retrieving <see cref="MigrationPerformanceEvidenceArtifact"/>
/// records so the admin UI and SDKs can render the latest published performance evidence and
/// browse the per-fixture history (issue #1033 slice 5).
/// </summary>
/// <remarks>
/// <para>
/// The store is the persistence seam behind the
/// <c>/api/v1/admin/migration/performance-evidence</c> endpoints. Implementations must
/// preserve the deterministic fingerprint computed by
/// <see cref="MigrationPerformanceEvidenceBuilder"/>; the fingerprint is the durable
/// identity callers use to deduplicate publishes and cross-reference workflow runs.
/// </para>
/// <para>
/// Listing operations must return records in newest-first order keyed on
/// <see cref="MigrationPerformanceEvidenceRecord.GeneratedAt"/>. Implementations are
/// free to additionally filter by source family and fixture size.
/// </para>
/// </remarks>
public interface IMigrationPerformanceEvidenceStore
{
    /// <summary>
    /// Persist a published evidence record. When a record with the same
    /// <see cref="MigrationPerformanceEvidenceRecord.EvidenceId"/> already exists the
    /// existing record is replaced so re-publishing the same artifact remains
    /// idempotent.
    /// </summary>
    /// <param name="record">Record to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask SaveAsync(MigrationPerformanceEvidenceRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Return the most recent record whose aggregate status equals <c>Pass</c>, or
    /// <c>null</c> when no passing record has been published yet.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<MigrationPerformanceEvidenceRecord?> GetLatestPassingAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Return a single record by its stable evidence identifier, or <c>null</c>
    /// when no record matches.
    /// </summary>
    /// <param name="evidenceId">Stable evidence identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<MigrationPerformanceEvidenceRecord?> GetByIdAsync(
        string evidenceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Return per-fixture history pages in newest-first order. Optional
    /// <paramref name="sourceFamily"/> and <paramref name="fixtureSize"/> filters
    /// narrow the listing to a specific fixture cell.
    /// </summary>
    /// <param name="sourceFamily">Optional source family filter.</param>
    /// <param name="fixtureSize">Optional fixture size filter.</param>
    /// <param name="limit">Maximum number of records to return. Implementations must clamp to a safe maximum.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<IReadOnlyList<MigrationPerformanceEvidenceRecord>> GetHistoryAsync(
        string? sourceFamily,
        string? fixtureSize,
        int limit,
        CancellationToken cancellationToken = default);
}
