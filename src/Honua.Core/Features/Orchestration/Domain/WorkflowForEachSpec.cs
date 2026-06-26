// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Orchestration.Domain;

/// <summary>
/// Declares that a workflow step is a ForEach/iteration template: the engine unrolls it
/// into one concrete sub-step per item in <see cref="Items"/>, substituting the item
/// value into the step's plan inputs. Iteration is statically declared (the collection
/// is part of the definition) so the unroll is a pure, deterministic transform that
/// composes with the existing DAG without introducing cycles or non-determinism.
/// </summary>
/// <param name="Items">
/// The ordered collection iterated over. Each item yields one sub-step; ordering is
/// preserved so the unroll is deterministic. Must be non-empty.
/// </param>
/// <param name="ItemPlaceholder">
/// The token replaced by the current item value in the step plan's input values
/// (default <c>${item}</c>). Allows a single plan template to fan out across items.
/// </param>
/// <param name="MaxIterations">
/// Optional per-step bound on the iteration count. When set, a definition whose
/// <see cref="Items"/> count exceeds it fails validation, guarding against an
/// unbounded fan-out.
/// </param>
public sealed record WorkflowForEachSpec(
    IReadOnlyList<string> Items,
    string ItemPlaceholder = "${item}",
    int? MaxIterations = null)
{
    /// <summary>
    /// Absolute safety ceiling on the number of sub-steps a single ForEach may unroll
    /// into, independent of <see cref="MaxIterations"/>. Bounds the durable run size and
    /// the reconcile cost even if a stored definition somehow exceeds its declared bound.
    /// </summary>
    public const int HardIterationLimit = 1000;
}
