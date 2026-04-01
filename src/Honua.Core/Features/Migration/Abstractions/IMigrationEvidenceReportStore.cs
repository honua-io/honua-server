// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Migration.Domain;

namespace Honua.Core.Features.Migration.Abstractions;

/// <summary>
/// Stores immutable migration evidence reports and lists previously generated artifacts.
/// </summary>
public interface IMigrationEvidenceReportStore
{
    /// <summary>
    /// Stores a newly generated migration evidence report.
    /// </summary>
    /// <param name="report">Report to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task StoreAsync(MigrationEvidenceReport report, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a previously stored migration evidence report by its identifier.
    /// </summary>
    /// <param name="reportId">Report identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Stored report, or <see langword="null"/> when not found.</returns>
    Task<MigrationEvidenceReport?> GetAsync(Guid reportId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists stored migration evidence reports ordered by generation time descending.
    /// </summary>
    /// <param name="limit">Maximum number of rows to return.</param>
    /// <param name="offset">Row offset for pagination.</param>
    /// <param name="provider">Optional provider filter.</param>
    /// <param name="cutoverProfile">Optional cutover profile filter.</param>
    /// <param name="readiness">Optional readiness filter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Stored report summaries.</returns>
    Task<IReadOnlyList<MigrationEvidenceReportSummary>> ListAsync(
        int limit = 50,
        int offset = 0,
        MigrationEvidenceProvider? provider = null,
        MigrationCutoverProfile? cutoverProfile = null,
        MigrationReadinessState? readiness = null,
        CancellationToken cancellationToken = default);
}
