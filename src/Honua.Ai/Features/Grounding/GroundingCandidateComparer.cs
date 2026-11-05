// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Grounding.Domain;

namespace Honua.Ai.Grounding;

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
