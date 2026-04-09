// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Core.Tests.Features.Infrastructure.Monitoring;

public sealed class AdaptiveSamplerMlSamplingDecisionEngineTests
{
    [Fact]
    public void MakeSamplingDecision_UsesAdaptiveSamplingRateAsBaseRate()
    {
        var engine = new MLSamplingDecisionEngine(NullLogger.Instance);

        var decision = engine.MakeSamplingDecision(new MLSamplingContext
        {
            OperationName = "important-query",
            Importance = OperationImportance.Important,
            AdaptiveSamplingRate = 0.02
        });

        decision.BaseSamplingRate.Should().BeApproximately(0.02, 0.000001);
        decision.EffectiveSamplingRate.Should().BeLessThan(0.03);
    }
}
