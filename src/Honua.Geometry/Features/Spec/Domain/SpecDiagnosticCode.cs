// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Spec.Domain;

/// <summary>
/// Discriminator for the kind of issue reported by a
/// <see cref="SpecDiagnostic"/>. Stable codes let tooling (IntelliSense,
/// grounding, CI) branch on well-known categories without string matching
/// messages.
/// </summary>
public enum SpecDiagnosticCode
{
    /// <summary>Placeholder — never emitted.</summary>
    None = 0,

    /// <summary>Unexpected character, unterminated string, or other lexer issue.</summary>
    SyntaxError,

    /// <summary>Parse error such as a missing keyword or misplaced punctuation.</summary>
    ParseError,

    /// <summary>Duplicate identifier at the same binding level (two sources with the same id).</summary>
    DuplicateIdentifier,

    /// <summary>Top-level section required by the grammar is missing.</summary>
    MissingSection,

    /// <summary>Reference target cannot be resolved against the local spec or catalog.</summary>
    UnknownReference,

    /// <summary>Operator is not registered in the operator catalog.</summary>
    UnknownOperator,

    /// <summary>Input/output types do not match the operator signature.</summary>
    TypeMismatch,

    /// <summary>Operator invoked without a required parameter.</summary>
    MissingRequiredParameter,

    /// <summary>Distance/area on a geographic CRS, or other unit/CRS violation.</summary>
    CrsUnitMismatch,

    /// <summary>Spec grammar version is newer or otherwise incompatible with the server.</summary>
    UnsupportedGrammarVersion,

    /// <summary>Operator capability version drift between spec and catalog.</summary>
    CapabilityVersionDrift,

    /// <summary>Catalog could not be consulted; reference resolution degraded to structural-only.</summary>
    CatalogUnavailable
}
