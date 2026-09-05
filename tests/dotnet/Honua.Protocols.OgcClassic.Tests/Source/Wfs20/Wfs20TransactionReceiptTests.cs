// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Protocols.Ogc.Classic.Wfs20;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

public sealed class Wfs20TransactionReceiptTests
{
    [Fact]
    public void GetReceiptCommitState_PreservesEveryCanonicalOutcome()
    {
        Wfs20Handler.GetReceiptCommitState(EditOperationResult.Success(42)).Should().Be("true");
        Wfs20Handler.GetReceiptCommitState(EditOperationResult.Failure("rejected")).Should().Be("false");
        Wfs20Handler.GetReceiptCommitState(
                EditOperationResult.FailureWithUnknownCommitOutcome("commit acknowledgement was lost"))
            .Should().Be("unknown");
    }
}
