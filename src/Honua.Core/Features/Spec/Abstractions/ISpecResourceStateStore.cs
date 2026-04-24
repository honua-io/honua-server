// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Spec.Domain;

namespace Honua.Core.Features.Spec.Abstractions;

/// <summary>
/// Durable state store for named resource kinds (<see cref="SpecResourceKind.Dataset"/>,
/// <see cref="SpecResourceKind.Service"/>, <see cref="SpecResourceKind.App"/>).
/// </summary>
/// <remarks>
/// The slot is reserved for S2. The S1 implementation throws
/// <see cref="SpecExecutionException"/> with
/// <see cref="SpecDiagnosticCodes.SpecKindNotInS1"/> on write calls;
/// <see cref="ReadCurrentAsync"/> returns <c>null</c> because no entries have
/// been written. The S1 plan / apply surface does not consult this store —
/// planner rejects <c>dataset</c> / <c>service</c> / <c>app</c> nodes with
/// <c>spec-kind-not-in-s1</c> purely from the declared kind, and the
/// orchestrator hard-fails the same kinds before dispatching. The
/// <c>unknown-service</c> / <c>unknown-reference</c> diagnostics stay with
/// the planner's unresolved <c>@</c> reference handling in S1.
/// </remarks>
public interface ISpecResourceStateStore
{
    /// <summary>
    /// Kind this store covers.
    /// </summary>
    SpecResourceKind Kind { get; }

    /// <summary>
    /// Returns the currently recorded state for the named resource, or
    /// <c>null</c> when the slot has no entry.
    /// </summary>
    Task<SpecResourceState?> ReadCurrentAsync(string resourceName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records or updates the state of a named resource.
    /// </summary>
    Task UpsertAsync(SpecResourceState state, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the named resource's durable state.
    /// </summary>
    Task DestroyAsync(string resourceName, CancellationToken cancellationToken = default);
}

/// <summary>
/// Durable state record for a named spec resource.
/// </summary>
public sealed record SpecResourceState
{
    /// <summary>Kind of resource.</summary>
    public required SpecResourceKind Kind { get; init; }

    /// <summary>Name (unique within kind).</summary>
    public required string Name { get; init; }

    /// <summary>Semantic version assigned to this entry.</summary>
    public required string Version { get; init; }

    /// <summary>Content hash of the producing compute node.</summary>
    public required string ContentHash { get; init; }

    /// <summary>UTC timestamp the record was produced.</summary>
    public required DateTimeOffset ProducedAt { get; init; }

    /// <summary>Arbitrary metadata attached to the record.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
