// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Reporting.Abstractions;
using Honua.Core.Features.Reporting.Domain;

namespace Honua.Core.Features.Reporting.Services;

/// <summary>
/// Always-available narrative provider that returns the deterministic baseline
/// text from each <see cref="NarrativeSlot"/>. Acts as the safety net the
/// builder runs first so every slot has factual text before any LLM call.
/// </summary>
internal sealed class DeterministicNarrativeProvider : INarrativeProvider
{
    /// <inheritdoc />
    public bool IsDeterministic => true;

    /// <inheritdoc />
    public Task<NarrativeFill> GenerateAsync(AnalysisReportDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var fill = new Dictionary<string, string>(draft.NarrativeSlots.Count, StringComparer.Ordinal);
        foreach (var slot in draft.NarrativeSlots)
        {
            fill[slot.SlotId] = slot.DeterministicText;
        }

        return Task.FromResult(new NarrativeFill { SlotText = fill });
    }
}
