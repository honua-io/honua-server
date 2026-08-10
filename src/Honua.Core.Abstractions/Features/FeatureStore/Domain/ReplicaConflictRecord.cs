// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// Classification of a disconnected-sync conflict. Mirrors the Esri offline/disconnected
/// editing conflict taxonomy but is exposed as a Honua product contract rather than an
/// ArcGIS-named enumeration (#1167).
/// </summary>
public enum ReplicaConflictType
{
    /// <summary>Client and server changed overlapping attribute values for the same feature.</summary>
    Attribute = 0,

    /// <summary>Client and server changed the geometry of the same feature.</summary>
    Geometry = 1,

    /// <summary>Client deleted a feature the server updated since the base checkpoint.</summary>
    DeleteUpdate = 2,

    /// <summary>Client updated a feature the server deleted since the base checkpoint.</summary>
    UpdateDelete = 3,

    /// <summary>Client inserted a feature whose stable key already exists on the server.</summary>
    DuplicateInsert = 4,

    /// <summary>Conflicting attachment add/replace/delete for the same feature.</summary>
    Attachment = 5,

    /// <summary>Conflicting related-record edit that cannot be applied independently.</summary>
    Relationship = 6,
}

/// <summary>
/// Lifecycle status of a durable conflict record. A conflict is <see cref="Pending"/> when first
/// recorded and transitions to a terminal status once an operator submits a resolution.
/// </summary>
public enum ReplicaConflictStatus
{
    /// <summary>Conflict has been recorded but not yet reviewed/resolved.</summary>
    Pending = 0,

    /// <summary>Conflict was resolved by applying an operator-selected resolution.</summary>
    Resolved = 1,

    /// <summary>Conflict review was explicitly deferred and may be revisited later.</summary>
    Deferred = 2,
}

/// <summary>
/// Resolution action an operator may apply to a pending conflict. The chosen action determines
/// whether a new committed server state is produced (#1167).
/// </summary>
public enum ReplicaConflictResolutionAction
{
    /// <summary>Apply the client's edit, overwriting the conflicting server state.</summary>
    AcceptClient = 0,

    /// <summary>Discard the client's edit and keep the current server state.</summary>
    KeepServer = 1,

    /// <summary>Apply an operator-supplied merge of client and server field values.</summary>
    MergeFields = 2,

    /// <summary>Apply an operator-selected geometry (client or server) for a geometry conflict.</summary>
    ChooseGeometry = 3,

    /// <summary>Reject the client edit and record the rejection as audit evidence.</summary>
    RejectClient = 4,

    /// <summary>Defer the decision; the conflict remains reviewable.</summary>
    Defer = 5,
}

/// <summary>
/// Durable record of a single disconnected-sync conflict produced when a replica upload could not
/// be applied cleanly against current server state. Persisted so the conflict can be reviewed and
/// resolved after the synchronize response has returned (#1167). Base/client/server feature states
/// are stored as opaque pre-serialized JSON so the conflict-review surface stays decoupled from the
/// physical feature schema and from any ArcGIS-specific payload shape.
/// </summary>
public readonly record struct ReplicaConflictRecord
{
    /// <summary>Unique conflict identifier (GUID hex).</summary>
    public required string ConflictId { get; init; }

    /// <summary>Replica the conflicting upload belonged to.</summary>
    public required string ReplicaId { get; init; }

    /// <summary>Service the replica belongs to.</summary>
    public required string ServiceId { get; init; }

    /// <summary>Service-local layer id of the conflicting feature.</summary>
    public required int LayerId { get; init; }

    /// <summary>
    /// Storage-layer id of the conflicting feature, as used by the change log. Persisted so a
    /// resolution can probe the change tracker for post-conflict edits without
    /// re-resolving metadata; null on records written before the staleness precondition existed, which
    /// simply skips that precondition (#2430).
    /// </summary>
    public int? StorageLayerId { get; init; }

    /// <summary>Stable object id of the conflicting feature.</summary>
    public required long ObjectId { get; init; }

    /// <summary>Conflict classification.</summary>
    public required ReplicaConflictType ConflictType { get; init; }

    /// <summary>Current lifecycle status.</summary>
    public required ReplicaConflictStatus Status { get; init; }

    /// <summary>
    /// Optional sync-operation correlation id linking the conflict to the synchronize call that
    /// produced it. Null when the producing operation was not correlated.
    /// </summary>
    public string? SyncOperationId { get; init; }

    /// <summary>
    /// Identifier of the device/client that uploaded the conflicting edit, when supplied.
    /// </summary>
    public string? DeviceId { get; init; }

    /// <summary>
    /// Identifier of the user that owns the conflicting edit, when known.
    /// </summary>
    public string? UserId { get; init; }

    /// <summary>
    /// Server generation cursor captured when the conflict was recorded. Links the conflict to the
    /// temporal change-set history (#1166). This is the replica's <em>base</em> generation, i.e. the
    /// cursor the upload was computed against — not the generation the conflict's own edit produced,
    /// which is <see cref="ResolutionBaseGeneration"/>.
    /// </summary>
    public required long ServerGeneration { get; init; }

    /// <summary>
    /// Server generation as of the moment this conflict's own sync batch finished touching its layer,
    /// i.e. the cursor the captured client/server states describe. A change to
    /// <c>(StorageLayerId, ObjectId)</c> after this generation is a <em>newer, post-conflict</em> edit,
    /// which is what makes a late resolution unsafe to apply (#2430).
    /// </summary>
    /// <remarks>
    /// Null when the conflict predates the staleness precondition or its layer's generation could not
    /// be stamped; the resolution surface then skips the precondition rather than blocking on a value
    /// it does not have, and says so in the code path.
    /// </remarks>
    public long? ResolutionBaseGeneration { get; init; }

    /// <summary>
    /// Whether the resolution's feature write has committed. Set immediately after the shared edit
    /// pipeline reports the write applied and before finalization begins, so a retry of an
    /// interrupted resolution knows whether it must still perform the write or only resume
    /// finalization — the write is never applied twice (#2430).
    /// </summary>
    public bool WriteCommitted { get; init; }

    /// <summary>
    /// Hash of the resolution inputs the claim was taken with — the action plus any operator-supplied
    /// field values or geometry side. A resume must match it, so a retry carrying different inputs
    /// cannot finalize the earlier write while reporting the new selection (#2430). Null on claims
    /// taken before the hash existed, which fall back to the operator/action check.
    /// </summary>
    public string? ResolutionInputHash { get; init; }

    /// <summary>
    /// Optimistic-concurrency token for the conflicting row as it was when this resolution was claimed,
    /// captured before the staleness precondition ran. A recovery that must re-apply an unmarked write
    /// uses it as the write's precondition instead of re-reading the row: a token derived at retry time
    /// would describe whatever is in the row now, including a foreign edit that landed during the lease,
    /// and the write would then overwrite it (#2430).
    /// </summary>
    public string? PreWriteStateToken { get; init; }

    /// <summary>
    /// Whether the resolution has been claimed but not yet finalized — its produced generation not
    /// persisted, or its audit evidence not written. Such a resolution is resumable: a retry completes
    /// the remaining finalization instead of short-circuiting to already-resolved, so an interruption
    /// after the feature write cannot leave the generation or audit trail permanently absent (#2430).
    /// </summary>
    /// <remarks>
    /// Expressed as "pending" rather than "finalized" so the default is the safe, terminal value:
    /// records written before the resume path existed, and any record built without this field, read
    /// as complete rather than perpetually resumable.
    /// </remarks>
    public bool FinalizationPending { get; init; }

    /// <summary>
    /// Whether the conflicting client edit was still committed to the layer when the conflict was
    /// detected. True under the last-write-wins conflict-handling mode (the client edit overwrote the
    /// concurrent server state and the record is advisory); false under manual review (the client edit
    /// was skipped and this record is the only carrier of the client intent). Resolution semantics
    /// depend on it: accepting the client is a no-op when the edit already landed, whereas keeping the
    /// server requires restoring the captured pre-conflict server state (#2430).
    /// </summary>
    public bool ClientEditApplied { get; init; }

    /// <summary>
    /// Whether the storage layer could not say if the conflicting client edit committed — the
    /// transaction's acknowledgement was lost, so the row may or may not carry the client state. When
    /// true, neither value of <see cref="ClientEditApplied"/> is trustworthy and the resolution planner
    /// must not take either of its no-op shortcuts: keeping the server restores the captured server
    /// state, and accepting the client writes the captured client state, so the resulting row matches
    /// the operator's decision whichever way the ambiguous write went (#2430).
    /// </summary>
    public bool ClientEditOutcomeUnknown { get; init; }

    /// <summary>
    /// Whether this conflict's own client edit committed but was then superseded by a later edit in the
    /// same upload to the same feature. <see cref="ClientEditApplied"/> is false for such a record
    /// because the row does not hold THIS edit's state — but it does not hold the captured pre-conflict
    /// server state either, so keeping the server has to perform a real restore rather than take the
    /// withheld-edit no-op shortcut (#2430).
    /// </summary>
    public bool ClientEditSuperseded { get; init; }

    /// <summary>
    /// Pre-serialized JSON for the base (common-ancestor) feature state, when known.
    /// </summary>
    /// <remarks>
    /// Left null by the GeoServices replica path: the server change log records only
    /// <c>(generation, layerId, objectId, operation)</c> with no per-change value snapshot, so the
    /// feature state as of the replica's base generation is not reconstructible server-side, and the
    /// Esri replica upload model carries no client-supplied base either. The conflict-review diff
    /// degrades to a two-way client-vs-server comparison in that case rather than inventing an
    /// ancestor. The field is kept because other replica-capable producers (and a future
    /// snapshot-carrying change log) can populate it.
    /// </remarks>
    public string? BaseStateJson { get; init; }

    /// <summary>Pre-serialized JSON for the incoming client feature state.</summary>
    public string? ClientStateJson { get; init; }

    /// <summary>Pre-serialized JSON for the current server feature state.</summary>
    public string? ServerStateJson { get; init; }

    /// <summary>Timestamp (UTC) when the conflict was first recorded.</summary>
    public required DateTimeOffset DetectedAt { get; init; }

    /// <summary>Resolution action applied, when the conflict has been resolved.</summary>
    public ReplicaConflictResolutionAction? ResolutionAction { get; init; }

    /// <summary>Identifier of the operator that resolved the conflict, when resolved.</summary>
    public string? ResolvedBy { get; init; }

    /// <summary>Timestamp (UTC) when the conflict was resolved, when resolved.</summary>
    public DateTimeOffset? ResolvedAt { get; init; }

    /// <summary>
    /// Server generation cursor produced by the resolution when a new committed server state was
    /// created. Null when the resolution did not produce a new server state (e.g. keep-server/defer).
    /// </summary>
    public long? ResolvedServerGeneration { get; init; }
}
