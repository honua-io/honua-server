// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Studio.Drafts;

/// <summary>
/// A single structural finding raised while building a deterministic package
/// draft (ADR-0076). Findings are typed rather than free prose so a protocol
/// adapter can project them without re-parsing a message string.
/// </summary>
/// <remarks>
/// Errors and warnings share this shape. The split follows the
/// generation-vs-publish leniency contract recorded in
/// <c>docs/internal/contributor/generation-families-retained-knowledge.md</c> §2:
/// structural defects block the draft, whereas unresolved references (a source
/// with no locator, for example) are deferred to publish time as warnings so a
/// partially specified composition still produces a draft.
/// </remarks>
public sealed record PackageDraftFinding
{
    /// <summary>
    /// Stable machine-readable code for the finding (for example
    /// <c>sourceIdMissing</c> or <c>crsUnsupported</c>).
    /// </summary>
    public required string Code { get; init; }

    /// <summary>
    /// JSON pointer into the draft request identifying the offending member
    /// (for example <c>/initialView/bbox</c>).
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Human-readable explanation of the finding.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Renders the finding as <c>code path: message</c> for inclusion in a
    /// protocol-level error message.
    /// </summary>
    /// <returns>The rendered finding.</returns>
    public string Describe() => $"{Code} {Path}: {Message}";
}
