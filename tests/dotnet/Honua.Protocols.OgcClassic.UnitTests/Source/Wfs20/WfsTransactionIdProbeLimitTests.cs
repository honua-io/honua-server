// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Protocols.Ogc.Classic.Wfs20.Services;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

public sealed class WfsTransactionIdProbeLimitTests
{
    [UnitTest]
    public void TransactionIdProbeLimit_UsesConfiguredEditLimits()
    {
        var limits = new LimitsOptions().Edits;
        limits.MaxFeaturesPerEdit.Should().BeLessThan(limits.MaxEditsPerTransaction);

        Wfs20Handler.TransactionIdProbeLimit(limits, preparedCount: 0, singleTarget: true).Should().Be(2);
        Wfs20Handler.TransactionIdProbeLimit(limits, preparedCount: 0, singleTarget: false)
            .Should().Be(limits.MaxFeaturesPerEdit + 1);
        Wfs20Handler.TransactionIdProbeLimit(limits, limits.MaxEditsPerTransaction - 1, singleTarget: false)
            .Should().Be(2);
        Wfs20Handler.TransactionIdProbeLimit(limits, limits.MaxEditsPerTransaction, singleTarget: false)
            .Should().Be(1);
    }
}
