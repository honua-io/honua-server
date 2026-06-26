// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Unit tests for the AWS Batch job-definition tier selector. The selector maps a job's
/// ephemeral-storage need onto the honua-iac job-definition pool (honua-iac#70) and resolves
/// the selected tier's ARN, while preserving the single-ARN back-compat path.
/// </summary>
public sealed class AwsBatchJobDefinitionTierSelectorTests
{
    private const string ArnS = "arn:aws:batch:us-west-2:123:job-definition/gp-s:1";
    private const string ArnM = "arn:aws:batch:us-west-2:123:job-definition/gp-m:1";
    private const string ArnL = "arn:aws:batch:us-west-2:123:job-definition/gp-l:1";
    private const string ArnXl = "arn:aws:batch:us-west-2:123:job-definition/gp-xl:1";

    // Inclusive-ceiling boundary cases mirroring the honua-devops sizing hint:
    // ≤20→s, ≤50→m, ≤100→l, ≤200→xl.
    [Theory]
    [InlineData(1, "s")]
    [InlineData(20, "s")]
    [InlineData(21, "m")]
    [InlineData(50, "m")]
    [InlineData(51, "l")]
    [InlineData(100, "l")]
    [InlineData(101, "xl")]
    [InlineData(200, "xl")]
    public void MapEphemeralGibToTier_UsesInclusiveCeilings(int ephemeralGib, string expectedTier)
    {
        AwsBatchJobDefinitionTierSelector.MapEphemeralGibToTier(ephemeralGib)
            .Should().Be(expectedTier);
    }

    [Theory]
    [InlineData(201)]
    [InlineData(500)]
    [InlineData(int.MaxValue)]
    public void MapEphemeralGibToTier_RejectsAboveCeiling(int ephemeralGib)
    {
        var act = () => AwsBatchJobDefinitionTierSelector.MapEphemeralGibToTier(ephemeralGib);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*exceeds*200 GiB*");
    }

    [Theory]
    [InlineData("20", ArnS)]
    [InlineData("21", ArnM)]
    [InlineData("50", ArnM)]
    [InlineData("51", ArnL)]
    [InlineData("100", ArnL)]
    [InlineData("101", ArnXl)]
    [InlineData("200", ArnXl)]
    public void ResolveJobDefinitionArn_SelectsTierArnFromEphemeralHint(string ephemeralGib, string expectedArn)
    {
        var parameters = TieredPool();
        parameters[AwsBatchParameterKeys.EphemeralGib] = ephemeralGib;

        AwsBatchJobDefinitionTierSelector.ResolveJobDefinitionArn(parameters)
            .Should().Be(expectedArn);
    }

    [Fact]
    public void ResolveJobDefinitionArn_DefaultsToSmallestTier_WhenNoEphemeralHint()
    {
        var parameters = TieredPool();

        AwsBatchJobDefinitionTierSelector.ResolveJobDefinitionArn(parameters)
            .Should().Be(ArnS);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("not-a-number")]
    public void ResolveJobDefinitionArn_DefaultsToSmallestTier_WhenHintNullOrInvalid(string ephemeralGib)
    {
        var parameters = TieredPool();
        parameters[AwsBatchParameterKeys.EphemeralGib] = ephemeralGib;

        AwsBatchJobDefinitionTierSelector.ResolveJobDefinitionArn(parameters)
            .Should().Be(ArnS);
    }

    [Fact]
    public void ResolveJobDefinitionArn_RejectsWhenEphemeralExceedsCeiling()
    {
        var parameters = TieredPool();
        parameters[AwsBatchParameterKeys.EphemeralGib] = "201";

        var act = () => AwsBatchJobDefinitionTierSelector.ResolveJobDefinitionArn(parameters);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*exceeds*200 GiB*");
    }

    [Fact]
    public void ResolveJobDefinitionArn_ThrowsWhenSelectedTierArnMissing()
    {
        // Pool missing the 'l' tier; a 100 GiB job needs 'l'.
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AwsBatchParameterKeys.JobDefinitionArnTierPrefix + "s"] = ArnS,
            [AwsBatchParameterKeys.JobDefinitionArnTierPrefix + "m"] = ArnM,
            [AwsBatchParameterKeys.JobDefinitionArnTierPrefix + "xl"] = ArnXl,
            [AwsBatchParameterKeys.EphemeralGib] = "100"
        };

        var act = () => AwsBatchJobDefinitionTierSelector.ResolveJobDefinitionArn(parameters);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{AwsBatchParameterKeys.JobDefinitionArnTierPrefix}l*not configured*");
    }

    [Fact]
    public void ResolveJobDefinitionArn_UsesSingleArn_WhenNoTierKeysConfigured()
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AwsBatchParameterKeys.JobDefinitionArn] = ArnS
        };

        AwsBatchJobDefinitionTierSelector.ResolveJobDefinitionArn(parameters)
            .Should().Be(ArnS);
    }

    [Fact]
    public void ResolveJobDefinitionArn_IgnoresEphemeralHint_OnSingleArnBackCompatPath()
    {
        // A non-tiered config with a stray ephemeral hint still returns the single ARN
        // rather than attempting tier resolution against keys that do not exist.
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AwsBatchParameterKeys.JobDefinitionArn] = ArnL,
            [AwsBatchParameterKeys.EphemeralGib] = "100"
        };

        AwsBatchJobDefinitionTierSelector.ResolveJobDefinitionArn(parameters)
            .Should().Be(ArnL);
    }

    [Fact]
    public void ResolveJobDefinitionArn_PrefersTierKeys_WhenBothSingleAndTierConfigured()
    {
        var parameters = TieredPool();
        parameters[AwsBatchParameterKeys.JobDefinitionArn] = "arn:aws:batch:us-west-2:123:job-definition/legacy:9";
        parameters[AwsBatchParameterKeys.EphemeralGib] = "100";

        AwsBatchJobDefinitionTierSelector.ResolveJobDefinitionArn(parameters)
            .Should().Be(ArnL);
    }

    [Fact]
    public void ResolveJobDefinitionArn_ThrowsWhenNeitherSingleNorTierConfigured()
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AwsBatchParameterKeys.JobQueueArn] = "arn:aws:batch:us-west-2:123:job-queue/gp"
        };

        var act = () => AwsBatchJobDefinitionTierSelector.ResolveJobDefinitionArn(parameters);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{AwsBatchParameterKeys.JobDefinitionArn}*");
    }

    private static Dictionary<string, string> TieredPool()
        => new(StringComparer.Ordinal)
        {
            [AwsBatchParameterKeys.JobDefinitionArnTierPrefix + "s"] = ArnS,
            [AwsBatchParameterKeys.JobDefinitionArnTierPrefix + "m"] = ArnM,
            [AwsBatchParameterKeys.JobDefinitionArnTierPrefix + "l"] = ArnL,
            [AwsBatchParameterKeys.JobDefinitionArnTierPrefix + "xl"] = ArnXl,
        };
}
