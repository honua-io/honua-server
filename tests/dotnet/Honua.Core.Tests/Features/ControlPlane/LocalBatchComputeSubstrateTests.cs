// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.ControlPlane;

public sealed class LocalBatchComputeSubstrateTests
{
    [UnitTest]
    public void Evaluate_SingleHost_IsCompatible()
    {
        var result = LocalBatchComputeSubstrate.Evaluate(BatchComputeSubstrateProfile.SingleHost, hasSharedWorkDir: false);
        result.IsCompatible.Should().BeTrue();
        result.Reason.Should().BeNull();
    }

    [UnitTest]
    public void Evaluate_Serverless_IsIncompatible()
    {
        var result = LocalBatchComputeSubstrate.Evaluate(BatchComputeSubstrateProfile.Serverless, hasSharedWorkDir: true);
        result.IsCompatible.Should().BeFalse("the local backends cannot survive a frozen/ephemeral serverless runtime");
        result.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [UnitTest]
    public void Evaluate_MultiNodeWithoutSharedWorkDir_IsIncompatible()
    {
        var result = LocalBatchComputeSubstrate.Evaluate(BatchComputeSubstrateProfile.MultiNode, hasSharedWorkDir: false);
        result.IsCompatible.Should().BeFalse("a sibling replica cannot observe an in-process job registry");
        result.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [UnitTest]
    public void Evaluate_MultiNodeWithSharedWorkDir_IsCompatible()
    {
        var result = LocalBatchComputeSubstrate.Evaluate(BatchComputeSubstrateProfile.MultiNode, hasSharedWorkDir: true);
        result.IsCompatible.Should().BeTrue("a shared work directory makes launch state reconstructable across nodes");
    }
}
