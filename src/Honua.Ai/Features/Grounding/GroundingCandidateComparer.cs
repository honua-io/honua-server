// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Grounding.Domain;

namespace Honua.Ai.Grounding;

/// <summary>
/// Shared comparer that makes grounding ranking fully deterministic. Scores are
/// rounded to 3 decimals before ranking, so ties are common; without an
/// ordinal secondary key equal-score candidates can reorder across runs and
/// shift clarification options, provenance order, and the chosen top
/// candidate. Every grounding sort site uses this comparer so the published
/// contract (`docs/developer/GROUNDING.md`) of deterministic ranking holds
/// regardless of input order.
/// </summary>
internal static class GroundingCandidateComparer
{
    public static readonly Comparison<GroundingCandidate> ByScoreDescending = Compare;

    private static int Compare(GroundingCandidate a, GroundingCandidate b)
    {
        var scoreCompare = b.Score.CompareTo(a.Score);
        if (scoreCompare != 0)
        {
            return scoreCompare;
        }

        var kindCompare = ((int)a.Kind).CompareTo((int)b.Kind);
        if (kindCompare != 0)
        {
            return kindCompare;
        }

        var idCompare = string.CompareOrdinal(a.Id, b.Id);
        if (idCompare != 0)
        {
            return idCompare;
        }

        return string.CompareOrdinal(a.DisplayName, b.DisplayName);
    }
}
