// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Reporting.Abstractions;
using Honua.Core.Features.Reporting.Domain;

namespace Honua.Core.Features.Reporting.Services;

internal sealed class DeterministicNarrativeProvider : INarrativeProvider
{
    public bool IsDeterministic => true;

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
