// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.ControlPlane;

public sealed class SubstrateProfileResolverTests
{
    [UnitTest]
    public void ResolveEffectiveProfile_AutoDetectsServerlessFromEnvironment()
    {
        var options = new SubstrateOptions { Profile = BatchComputeSubstrateProfile.SingleHost, AutoDetectServerless = true };

        var resolved = SubstrateProfileResolver.ResolveEffectiveProfile(
            options,
            name => name == "AWS_LAMBDA_FUNCTION_NAME" ? "my-func" : null);

        resolved.Should().Be(BatchComputeSubstrateProfile.Serverless, "a serverless runtime marker escalates the single-host default");
    }

    [UnitTest]
    public void ResolveEffectiveProfile_WhenAutoDetectDisabled_KeepsDeclaredProfile()
    {
        var options = new SubstrateOptions { Profile = BatchComputeSubstrateProfile.SingleHost, AutoDetectServerless = false };

        var resolved = SubstrateProfileResolver.ResolveEffectiveProfile(
            options,
            name => name == "FUNCTIONS_WORKER_RUNTIME" ? "dotnet-isolated" : null);

        resolved.Should().Be(BatchComputeSubstrateProfile.SingleHost);
    }

    [UnitTest]
    public void ResolveEffectiveProfile_ExplicitMultiNode_IsPreserved()
    {
        var options = new SubstrateOptions { Profile = BatchComputeSubstrateProfile.MultiNode };

        var resolved = SubstrateProfileResolver.ResolveEffectiveProfile(options, _ => null);

        resolved.Should().Be(BatchComputeSubstrateProfile.MultiNode);
    }
}
