// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Abstractions;

/// <summary>
/// Idempotent catalog writes used by migration apply paths to persist workspace
/// and layer-group catalog entries before per-layer publishing runs.
/// </summary>
/// <remarks>
/// This abstraction intentionally exposes only catalog-level upserts. Data-store
/// copying, per-layer publishing, and style persistence are handled by their own
/// services and are deferred to follow-on migration slices.
/// </remarks>
public interface IMigrationCatalogWriter
{
    /// <summary>
    /// Ensure that a Honua catalog service entry exists. Idempotent: returns
    /// <see cref="MigrationCatalogWriteOutcome.Created"/> on first apply and
    /// <see cref="MigrationCatalogWriteOutcome.AlreadyExists"/> on re-apply.
    /// </summary>
    /// <param name="connectionString">PostgreSQL connection string for the target catalog.</param>
    /// <param name="request">Service definition to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<MigrationCatalogWriteOutcome> EnsureCatalogServiceAsync(
        string connectionString,
        MigrationCatalogServiceRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Request payload for <see cref="IMigrationCatalogWriter.EnsureCatalogServiceAsync"/>.
/// </summary>
public sealed record MigrationCatalogServiceRequest
{
    /// <summary>
    /// Stable service name (case-insensitive, lower-case kebab/snake form).
    /// </summary>
    public required string ServiceName { get; init; }

    /// <summary>
    /// Human-readable description for the catalog row.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Default SRID for the service. The catalog requires a value; 4326 is used
    /// when the source did not declare one.
    /// </summary>
    public int Srid { get; init; } = 4326;

    /// <summary>
    /// Catalog entry kind, such as <c>workspace</c> or <c>layer-group</c>. Used in
    /// telemetry and not persisted by the default writer schema.
    /// </summary>
    public string EntryKind { get; init; } = "workspace";
}

/// <summary>
/// Outcome of an idempotent catalog write.
/// </summary>
public enum MigrationCatalogWriteOutcome
{
    /// <summary>
    /// The catalog entry was newly created by this write.
    /// </summary>
    Created,

    /// <summary>
    /// The catalog entry already existed; no mutation was performed.
    /// </summary>
    AlreadyExists
}
