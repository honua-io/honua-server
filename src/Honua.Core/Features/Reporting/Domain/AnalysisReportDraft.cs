// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Security.Admission;

namespace Honua.Core.Features.Reporting.Domain;

public sealed record AnalysisReportDraft
{
    // The model-invocation identity stays on the server and is never part of the report or caller JSON.
    [JsonIgnore]
    public AiExecutionContext? ExecutionContext { get; init; }

    public required string TemplateId { get; init; }

    public required string TemplateVersion { get; init; }

    public required string ProcessId { get; init; }

    public required string ProcessFamily { get; init; }

    public required IReadOnlyList<AnalysisReportSection> Sections { get; init; }

    // Every narrative slot has a deterministic paragraph; a model may replace that text one slot at a time.
    public required IReadOnlyList<NarrativeSlot> NarrativeSlots { get; init; }

    public required AnalysisResultPackage SourcePackage { get; init; }
}

public sealed record NarrativeSlot
{
    public required string SlotId { get; init; }

    public string? Heading { get; init; }

    // The factual paragraph must be safe to render verbatim, apart from the renderer's own escaping.
    public required string DeterministicText { get; init; }

    public string? LlmHint { get; init; }
}

public sealed record NarrativeFill
{
    public required IReadOnlyDictionary<string, string> SlotText { get; init; }

    public static NarrativeFill Empty { get; } = new()
    {
        SlotText = new Dictionary<string, string>(StringComparer.Ordinal)
    };
}
