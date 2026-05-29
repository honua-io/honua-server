// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Spec.Domain;

/// <summary>
/// Source range (1-based line/column + absolute offset + length) carried on
/// every spec AST node so that diagnostics can surface precise
/// line-and-column positions rather than only character offsets.
/// </summary>
/// <param name="Line">1-based start line.</param>
/// <param name="Column">1-based start column.</param>
/// <param name="Offset">0-based absolute character offset.</param>
/// <param name="Length">Length of the span in characters.</param>
public readonly record struct SourceSpan(int Line, int Column, int Offset, int Length)
{
    /// <summary>
    /// Synthetic empty span used for nodes emitted by the canonicalizer (where
    /// the original text is not known) and for the end-of-input sentinel.
    /// </summary>
    public static SourceSpan Synthetic { get; } = new(0, 0, 0, 0);

    /// <summary>
    /// Gets a value indicating whether this span points at real source text.
    /// </summary>
    public bool HasLocation => Line > 0 && Column > 0;
}
