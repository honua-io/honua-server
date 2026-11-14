// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Ai.Grounding;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Grounding;

public sealed class ClarificationAnswerResolverTests
{
    [UnitTheory]
    [InlineData("destructive.confirm", "true")]
    [InlineData("destructive.confirm", " Yes ")]
    [InlineData("workflow_family.blocked", "CONFIRMED")]
    [InlineData("workflow_family.blocked", "confirm")]
    public void Parse_AffirmativeConfirmation_AcknowledgesQuestion(string questionId, string answer)
    {
        Parse(questionId, answer).AppliedQuestionIds.Should().Contain(questionId);
    }

    [UnitTheory]
    [InlineData("destructive.confirm", "false")]
    [InlineData("destructive.confirm", "no")]
    [InlineData("destructive.confirm", "0")]
    [InlineData("workflow_family.blocked", "False")]
    [InlineData("workflow_family.blocked", "cancel")]
    public void Parse_NegativeOrUnrecognisedConfirmation_LeavesQuestionUnanswered(string questionId, string answer)
    {
        Parse(questionId, answer).AppliedQuestionIds.Should().NotContain(questionId);
    }

    private static AppliedClarificationAnswers Parse(string questionId, string answer) =>
        ClarificationAnswerResolver.Parse(new ClarificationResponse
        {
            IntentId = "intent-abc",
            Answers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { [questionId] = [answer] },
        });
}
