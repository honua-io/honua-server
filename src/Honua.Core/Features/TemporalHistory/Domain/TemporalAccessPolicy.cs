// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.TemporalHistory.Domain;

/// <summary>
/// History-specific authorization for a layer's temporal-history surfaces. Each operation carries an
/// independent role set so current reads, history reads, diff reads, timeline reads, rollback planning,
/// and rollback execution can be governed separately. Unset role sets fall back to the layer's general
/// access policy (history reads default to the same roles as current reads).
/// </summary>
public sealed record TemporalAccessPolicy
{
    /// <summary>
    /// Whether anonymous principals may read history (as-of and checkpoints).
    /// </summary>
    public bool AllowAnonymousHistoryRead { get; init; }

    /// <summary>
    /// Roles permitted to read history (as-of and checkpoints). Null falls back to current-read roles.
    /// </summary>
    public string[]? HistoryReadRoles { get; init; }

    /// <summary>
    /// Roles permitted to read diffs. Null falls back to history-read roles.
    /// </summary>
    public string[]? DiffReadRoles { get; init; }

    /// <summary>
    /// Roles permitted to read per-feature timelines. Null falls back to history-read roles.
    /// </summary>
    public string[]? TimelineReadRoles { get; init; }

    /// <summary>
    /// Roles permitted to generate rollback plans. Null falls back to history-read roles.
    /// </summary>
    public string[]? RollbackPlanRoles { get; init; }

    /// <summary>
    /// Roles permitted to execute approved rollbacks. Null falls back to the layer's write roles.
    /// </summary>
    public string[]? RollbackExecuteRoles { get; init; }

    /// <summary>
    /// When true, actor/source attribution is omitted from timeline and diff responses.
    /// </summary>
    public bool MaskAttribution { get; init; }
}
