// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.FieldWorkflows.Export;

/// <summary>
/// Durable store for back-office field export packages (#1160). Selects the
/// reviewed submission record set matching an export filter and persists an
/// audited <see cref="FieldExportRecord"/> describing each generated package.
/// </summary>
public interface IFieldExportStore
{
    /// <summary>
    /// Selects the submission record set matching <paramref name="request"/> and
    /// persists a durable export record. Returns the materialized package
    /// (selected records plus the persisted export record).
    /// </summary>
    Task<FieldExportPackage> CreateExportAsync(
        FieldExportRequest request,
        string requestedBy,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists previously generated export records, newest first.
    /// </summary>
    Task<FieldExportRecordListResult> ListExportsAsync(
        int limit,
        int offset,
        CancellationToken cancellationToken = default);
}
