// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Console.Domain;

namespace Honua.Core.Features.Console.Abstractions;

/// <summary>
/// Validates that an item's transitive provenance closure is shareable by a
/// target audience before public sharing or embed enablement is committed.
/// </summary>
/// <remarks>
/// Public sharing an item that references private provenance would silently
/// expose, or dangle, those references. The validator walks the provenance graph
/// and reports every dependency that is incompatible with the requested tier so
/// the endpoint can return a stable, machine-readable conflict.
/// </remarks>
public interface IConsoleDependencyClosureValidator
{
    /// <summary>
    /// Returns the dependencies in <paramref name="itemId"/>'s provenance closure
    /// (up to <paramref name="maxDepth"/>) that are not shareable by
    /// <paramref name="targetTier"/>. An empty list means the closure is
    /// compatible. The root item itself is not validated.
    /// </summary>
    Task<IReadOnlyList<ConsoleShareDependencyConflict>> ValidateAsync(
        string itemId,
        ConsoleShareAccessTier targetTier,
        int maxDepth,
        CancellationToken cancellationToken = default);
}
