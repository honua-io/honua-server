// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Spec.Domain;

namespace Honua.Core.Features.Spec.Abstractions;

/// <summary>
/// Parses spec source text into an AST. User-facing syntax issues are
/// returned as <see cref="SpecDiagnostic"/> values on
/// <see cref="SpecParseResult"/>; implementations must not throw for malformed
/// input. Exceptions are reserved for internal invariant violations.
/// </summary>
public interface ISpecParser
{
    /// <summary>
    /// Parses <paramref name="text"/> and returns a partial-or-complete AST
    /// plus any diagnostics collected during lex/parse.
    /// </summary>
    /// <param name="text">Spec source text.</param>
    /// <returns>Parse result.</returns>
    SpecParseResult Parse(string text);
}
