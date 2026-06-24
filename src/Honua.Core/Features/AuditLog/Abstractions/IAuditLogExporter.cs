// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.AuditLog.Abstractions;

/// <summary>
/// Streaming export surface over the audit trail for SIEM ingestion (#509).
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="IAuditLogReader"/>: the reader serves small,
/// cursor-paged Console queries, whereas the exporter streams an entire
/// time-bounded slice of the trail oldest-first so a SIEM can ingest it as a
/// continuous JSON Lines (or CEF) feed without buffering the whole result set
/// in memory. Oldest-first ordering lets a SIEM checkpoint on the last consumed
/// <see cref="AuditEventRecord.AuditId"/> and resume incrementally.
/// </para>
/// </remarks>
public interface IAuditLogExporter
{
    /// <summary>
    /// Streams audit records matching the export filter, ordered oldest-first
    /// by sink-assigned identifier.
    /// </summary>
    /// <param name="filter">Time-range and identifier filter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async stream of audit records.</returns>
    IAsyncEnumerable<AuditEventRecord> ExportAsync(
        AuditExportFilter filter,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Filter for <see cref="IAuditLogExporter.ExportAsync"/>.
/// </summary>
public sealed record AuditExportFilter
{
    /// <summary>Lower bound (inclusive) for <see cref="AuditEvent.Timestamp"/>.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Upper bound (exclusive) for <see cref="AuditEvent.Timestamp"/>.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>Optional actor filter (exact match).</summary>
    public string? Actor { get; init; }

    /// <summary>Optional resource type filter (exact match).</summary>
    public string? ResourceType { get; init; }

    /// <summary>Optional dotted action filter (exact match).</summary>
    public string? Action { get; init; }

    /// <summary>
    /// Hard upper bound on the number of records streamed. Implementations clamp
    /// to a server-side maximum to protect the database from an unbounded scan.
    /// </summary>
    public int? Limit { get; init; }
}
