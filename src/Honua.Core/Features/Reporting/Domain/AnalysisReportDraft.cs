// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Reporting.Domain;

/// <summary>
/// Pre-narrative state produced by an
/// <see cref="Honua.Core.Features.Reporting.Abstractions.IAnalysisReportTemplate"/>.
/// Holds the structural sections plus narrative slot definitions that providers
/// fill before the final <see cref="AnalysisReport"/> is composed.
/// </summary>
public sealed record AnalysisReportDraft
{
    /// <summary>Identifier of the template that produced this draft.</summary>
    public required string TemplateId { get; init; }

    /// <summary>Version of the template that produced this draft.</summary>
    public required string TemplateVersion { get; init; }

    /// <summary>Process identifier the template targets.</summary>
    public required string ProcessId { get; init; }

    /// <summary>Process family slice (segment before the first <c>.</c>).</summary>
    public required string ProcessFamily { get; init; }

    /// <summary>
    /// Structural sections excluding narrative blocks. The builder appends
    /// <see cref="NarrativeSection"/> entries into the final report by walking
    /// <see cref="NarrativeSlots"/> in declared order.
    /// </summary>
    public required IReadOnlyList<AnalysisReportSection> Sections { get; init; }

    /// <summary>
    /// Narrative slot definitions in declared order. Each slot must have a
    /// deterministic factual paragraph supplied by the template; LLM enrichment
    /// replaces the deterministic text on a slot-by-slot basis.
    /// </summary>
    public required IReadOnlyList<NarrativeSlot> NarrativeSlots { get; init; }

    /// <summary>Originating result package; useful to providers that need raw stats.</summary>
    public required AnalysisResultPackage SourcePackage { get; init; }
}

/// <summary>
/// Declarative narrative-slot record. Templates produce slots so the
/// deterministic and LLM paths can compose the same section list.
/// </summary>
public sealed record NarrativeSlot
{
    /// <summary>Stable slot identifier (template-scoped).</summary>
    public required string SlotId { get; init; }

    /// <summary>Optional heading rendered immediately before the slot.</summary>
    public string? Heading { get; init; }

    /// <summary>
    /// Always-available factual paragraph composed by the deterministic provider.
    /// Renderers fall back to this text whenever an LLM provider is unavailable
    /// or fails. Must be safe to render verbatim with no further escaping
    /// beyond the format-specific escaping the renderer performs.
    /// </summary>
    public required string DeterministicText { get; init; }

    /// <summary>
    /// Short, content-summary prompt the LLM provider may use to enrich the
    /// slot. Not rendered to operators directly.
    /// </summary>
    public string? LlmHint { get; init; }
}

/// <summary>
/// Narrative-fill response. Maps <see cref="NarrativeSlot.SlotId"/> to the
/// LLM-authored text that should replace the deterministic baseline.
/// </summary>
public sealed record NarrativeFill
{
    /// <summary>
    /// Per-slot LLM text. Slots absent from this map keep the deterministic
    /// text from <see cref="AnalysisReportDraft.NarrativeSlots"/>.
    /// </summary>
    public required IReadOnlyDictionary<string, string> SlotText { get; init; }

    /// <summary>
    /// Empty fill — every slot keeps its deterministic text.
    /// </summary>
    public static NarrativeFill Empty { get; } = new()
    {
        SlotText = new Dictionary<string, string>(StringComparer.Ordinal)
    };
}
