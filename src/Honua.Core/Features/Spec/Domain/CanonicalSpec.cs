// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Spec.Domain;

/// <summary>
/// Canonical spec document produced by the grammar / validator pipeline and
/// consumed by the plan / apply engine.
/// </summary>
/// <remarks>
/// The engine treats this document as read-only input. It does not validate
/// grammar; cycle detection, kind validation, and topological layout are
/// performed by <see cref="Honua.Core.Features.Spec.Abstractions.ISpecPlanner"/>.
/// </remarks>
public sealed record CanonicalSpecDocument
{
    /// <summary>
    /// Canonical grammar version identifier. Participates in the content-hash
    /// cache key so specs authored against a different grammar do not share
    /// cache entries.
    /// </summary>
    public required string GrammarVersion { get; init; }

    /// <summary>
    /// Process-family version identifier. Participates in the content-hash
    /// cache key so that process-family updates invalidate cached outputs.
    /// </summary>
    public required string ProcessFamilyVersion { get; init; }

    /// <summary>
    /// Optional stable identifier for the spec document; not part of the cache
    /// key but surfaced in telemetry and apply-event correlation.
    /// </summary>
    public string? SpecId { get; init; }

    /// <summary>
    /// Declared spec nodes in author order.
    /// </summary>
    public required IReadOnlyList<CanonicalSpecNode> Nodes { get; init; }
}

/// <summary>
/// A single node within a <see cref="CanonicalSpecDocument"/>.
/// </summary>
public sealed record CanonicalSpecNode
{
    /// <summary>
    /// Stable identifier unique within the document.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Canonical resource kind; see <see cref="SpecResourceKind"/>.
    /// </summary>
    public required SpecResourceKind Kind { get; init; }

    /// <summary>
    /// Declared operator identifier (e.g. <c>compute.buffer</c>).
    /// Null when the node is a pure data source with no operator invocation.
    /// </summary>
    public string? Op { get; init; }

    /// <summary>
    /// Input bindings — keys are parameter names; values are canonical
    /// tokens (e.g. <c>@other-node</c>, layer refs, scalar JSON literals).
    /// </summary>
    public IReadOnlyDictionary<string, string> Inputs { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Flat parameter bag captured from the spec body.
    /// </summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Canonicalised JSON fragment for this node. Participates in the
    /// content-hash cache key; upstream is expected to write the same bytes
    /// for semantically identical nodes.
    /// </summary>
    public string? CanonicalFragment { get; init; }

    /// <summary>
    /// Optional declared source pins; when supplied, the source-pin metadata
    /// contributes to the cache key instead of triggering
    /// <c>mutable-source-no-pin</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string> SourcePins { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Whether the declared op is non-deterministic (sampling, time-based,
    /// etc.). The planner surfaces this as a <c>nondeterministic-op</c>
    /// warning.
    /// </summary>
    public bool Nondeterministic { get; init; }
}

/// <summary>
/// Canonical resource kinds recognised by the plan / apply engine.
/// </summary>
public enum SpecResourceKind
{
    /// <summary>
    /// Ephemeral compute / report node — output is content-hash cached.
    /// </summary>
    Compute,

    /// <summary>
    /// Durable report output — treated as ephemeral cache in S1.
    /// </summary>
    Report,

    /// <summary>
    /// Durable dataset resource. Slot reserved for S2; apply rejects in S1.
    /// </summary>
    Dataset,

    /// <summary>
    /// Durable service resource. Slot reserved for S2; apply rejects in S1.
    /// </summary>
    Service,

    /// <summary>
    /// Durable application resource. Slot reserved for S2; apply rejects in S1.
    /// </summary>
    App
}
