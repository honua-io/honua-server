// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.TemporalHistory.Domain;

namespace Honua.Core.Features.TemporalHistory.Abstractions;

/// <summary>
/// Read-and-plan contract for a layer's temporal data history. Implementations expose history "like
/// git over data" — capability discovery, as-of point-in-time reads, checkpoint enumeration, diffs,
/// per-feature timelines, rollback planning, and append-only corrective rollback — without leaking the
/// underlying temporal-table or audit-log implementation to callers. This is adjacent to, and
/// independently deployable from, named-version reconcile/post (honua-server#371).
/// </summary>
public interface ITemporalHistorySource
{
    /// <summary>
    /// Discovers the temporal capabilities of a layer, combining operator configuration with runtime
    /// checks (for example withdrawing as-of support when the supporting index is absent).
    /// </summary>
    /// <param name="layer">The configured layer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The capability set, or null when the layer declares no temporal source.</returns>
    Task<TemporalSourceCapabilityInfo?> GetCapabilitiesAsync(
        LayerDefinition layer,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves an opaque cursor to its UTC instant for the supplied layer.
    /// </summary>
    /// <param name="layer">The configured layer.</param>
    /// <param name="cursor">The cursor to resolve.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved UTC instant, or null when the cursor cannot be resolved.</returns>
    Task<DateTimeOffset?> ResolveCursorAsync(
        LayerDefinition layer,
        TemporalCursor cursor,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists named/derived checkpoints for a layer.
    /// </summary>
    /// <param name="layer">The configured layer.</param>
    /// <param name="limit">Maximum number of checkpoints to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The available checkpoints, newest first.</returns>
    Task<IReadOnlyList<TemporalCheckpoint>> ListCheckpointsAsync(
        LayerDefinition layer,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the deterministic state of a layer as of a cursor.
    /// </summary>
    /// <param name="layer">The configured layer.</param>
    /// <param name="at">The cursor to read as of.</param>
    /// <param name="page">Paging request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The point-in-time snapshot page.</returns>
    Task<TemporalSnapshot> QueryAsOfAsync(
        LayerDefinition layer,
        TemporalCursor at,
        TemporalPageRequest page,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes a diff between two checkpoints.
    /// </summary>
    /// <param name="layer">The configured layer.</param>
    /// <param name="fromCursor">The source cursor.</param>
    /// <param name="toCursor">The target cursor.</param>
    /// <param name="page">Paging request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The diff with summary counts and a page of feature changes.</returns>
    Task<TemporalDiff> DiffAsync(
        LayerDefinition layer,
        TemporalCursor fromCursor,
        TemporalCursor toCursor,
        TemporalPageRequest page,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a single feature's revision timeline. Attribution masking is applied inside the
    /// implementation according to the layer's temporal access policy so it cannot be bypassed by callers.
    /// </summary>
    /// <param name="layer">The configured layer.</param>
    /// <param name="featureId">Stable feature identifier.</param>
    /// <param name="page">Paging request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The feature timeline page.</returns>
    Task<TemporalTimeline> GetTimelineAsync(
        LayerDefinition layer,
        string featureId,
        TemporalPageRequest page,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a rollback plan describing whether and how the layer can be rolled back to a cursor.
    /// </summary>
    /// <param name="layer">The configured layer.</param>
    /// <param name="toCursor">The target cursor.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The rollback plan.</returns>
    Task<TemporalRollbackPlan> PlanRollbackAsync(
        LayerDefinition layer,
        TemporalCursor toCursor,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes an approved rollback as a forward corrective operation, appending corrective rows and
    /// stamping a new checkpoint. History is never deleted.
    /// </summary>
    /// <param name="layer">The configured layer.</param>
    /// <param name="toCursor">The target cursor to restore.</param>
    /// <param name="context">Governing job, actor, and correlation context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The rollback result including the new checkpoint cursor.</returns>
    Task<TemporalRollbackResult> ExecuteRollbackAsync(
        LayerDefinition layer,
        TemporalCursor toCursor,
        TemporalRollbackContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Raised for client-correctable temporal-history conditions (for example an unsupported operation,
/// an unresolvable cursor, or a blocked rollback). Carries only client-safe messages; raw SQL, table
/// names, and provider internals must never be placed in <see cref="System.Exception.Message"/>.
/// </summary>
public sealed class TemporalHistoryException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TemporalHistoryException"/> class.
    /// </summary>
    /// <param name="message">Client-safe message.</param>
    public TemporalHistoryException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TemporalHistoryException"/> class.
    /// </summary>
    /// <param name="message">Client-safe message.</param>
    /// <param name="innerException">The underlying cause (not surfaced to clients).</param>
    public TemporalHistoryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
