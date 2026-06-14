// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Temporal.Domain;

/// <summary>
/// The category of source that produced a recorded change (slice 4 of honua-server#1166). Lets clients
/// link a revision back to the system that produced it without parsing free-form source identifiers.
/// </summary>
public enum TemporalAttributionSource
{
    /// <summary>Source not recorded (changes predating attribution capture, or no GUC supplied).</summary>
    Unknown = 0,

    /// <summary>Interactive feature edit (FeatureServer applyEdits, OGC API transaction, OData CRUD).</summary>
    EditSession = 1,

    /// <summary>Bulk import / ETL job.</summary>
    Import = 2,

    /// <summary>Geoprocessing or analytical job.</summary>
    Job = 3,

    /// <summary>GitOps metadata release.</summary>
    Release = 4,

    /// <summary>Replica synchronization (offline edit upload).</summary>
    ReplicaSync = 5,

    /// <summary>Governed rollback corrective operation (slice 5).</summary>
    Rollback = 6
}

/// <summary>
/// Actor/source/operation attribution for a recorded change (slice 4 of honua-server#1166): who changed
/// what, from which system, and which higher-level operation it belonged to. Populated from a
/// transaction-scoped attribution context recorded alongside the change log; any field may be null when
/// the producing path did not supply it (including all changes predating attribution capture).
/// </summary>
/// <param name="Actor">Stable identifier of the principal that made the change (user/service account).</param>
/// <param name="Source">The category of system that produced the change.</param>
/// <param name="Operation">The higher-level operation name (for example <c>applyEdits</c>, <c>import</c>, <c>rollback</c>).</param>
/// <param name="SourceId">
/// The producing source's correlation id when applicable: a job id, edit-session id, GitOps release id,
/// or audit-event id, depending on <paramref name="Source"/>.
/// </param>
public sealed record TemporalAttribution(
    string? Actor,
    TemporalAttributionSource Source,
    string? Operation,
    string? SourceId);
