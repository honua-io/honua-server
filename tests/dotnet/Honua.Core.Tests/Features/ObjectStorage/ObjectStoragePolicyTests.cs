// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ObjectStorage;

namespace Honua.Core.Tests.Features.ObjectStorage;

public sealed class ObjectStoragePolicyTests
{
    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("abc")]
    [InlineData("customer-imports")]
    [InlineData("data.example.com")]
    [InlineData("a1b2c3")]
    public void BucketName_FollowsTheS3NamingRules(string bucket)
        => ObjectStoragePolicy.IsValidBucketName(bucket).Should().BeTrue();

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(null)]
    [InlineData("ab")]
    [InlineData("Customer")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("two..dots")]
    [InlineData("192.168.1.10")]
    [InlineData("xn--punycode")]
    [InlineData("sthree-reserved")]
    [InlineData("amzn-s3-demo-bucket")]
    [InlineData("access-point-s3alias")]
    [InlineData("lambda--ol-s3")]
    [InlineData("region.mrap")]
    [InlineData("directory--x-s3")]
    [InlineData("under_score")]
    [InlineData("host/evil")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void BucketName_RefusesNamesS3Rejects(string? bucket)
        => ObjectStoragePolicy.IsValidBucketName(bucket).Should().BeFalse();

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("us-east-1")]
    [InlineData("eu-central-2")]
    [InlineData("ap-southeast-7")]
    public void Region_FromTheFixedListIsAccepted(string region)
        => ObjectStoragePolicy.IsKnownRegion(region).Should().BeTrue();

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("us-east-1.evil.example")]
    [InlineData("cn-north-1")]
    [InlineData("US-EAST-1")]
    [InlineData("")]
    public void Region_OutsideTheFixedListIsRefused(string region)
        => ObjectStoragePolicy.IsKnownRegion(region).Should().BeFalse();

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("arn:aws:iam::123456789012:role/honua-connection-imports")]
    [InlineData("arn:aws:iam::123456789012:role/service/honua-connection-imports")]
    public void RoleArn_NamingAHonuaConnectionRoleIsAccepted(string roleArn)
        => ObjectStoragePolicy.IsValidRoleArn(roleArn).Should().BeTrue();

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("arn:aws:iam::123456789012:role/imports")]
    [InlineData("arn:aws:iam::12345678901:role/honua-connection-imports")]
    [InlineData("arn:aws:iam::123456789012:user/honua-connection-imports")]
    [InlineData("arn:aws-cn:iam::123456789012:role/honua-connection-imports")]
    [InlineData("arn:aws:iam::123456789012:role/honua-connection-")]
    [InlineData("arn:aws:iam::123456789012:role/honua-connection-imports\n")]
    public void RoleArn_OtherRolesAreRefused(string roleArn)
        => ObjectStoragePolicy.IsValidRoleArn(roleArn).Should().BeFalse();

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("parcels", "parcels/")]
    [InlineData("parcels/2026/", "parcels/2026/")]
    public void Prefix_IsNormalisedToATrailingSlash(string? prefix, string expected)
    {
        ObjectStoragePolicy.NormalizePrefix(prefix).Should().Be(expected);
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("/parcels")]
    [InlineData("parcels/../secret")]
    [InlineData("parcels//2026")]
    [InlineData("./parcels")]
    [InlineData("parcels/*")]
    [InlineData("parcels/${aws:username}")]
    [InlineData("s3://bucket/parcels")]
    [InlineData("/vsis3/bucket/parcels")]
    public void Prefix_RefusesPathsThatEscapeOrWiden(string prefix)
        => ObjectStoragePolicy.NormalizePrefix(prefix).Should().BeNull();

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("parcels/", "parcels/2026/lots.geojson")]
    [InlineData("", "lots.geojson")]
    public void Key_UnderTheConnectionPrefixIsAccepted(string connectionPrefix, string key)
        => ObjectStoragePolicy.IsKeyUnderPrefix(connectionPrefix, key).Should().BeTrue();

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("parcelsx/lots.geojson")]
    [InlineData("other/lots.geojson")]
    [InlineData("parcels/")]
    [InlineData("parcels/../secret.geojson")]
    [InlineData("/parcels/lots.geojson")]
    [InlineData("parcels/a/vsicurl/x")]
    [InlineData("parcels/http://evil")]
    [InlineData("parcels/lots\u0001.geojson")]
    [InlineData("parcels\\lots.geojson")]
    public void Key_OutsideTheConnectionPrefixOrUnsafeIsRefused(string key)
        => ObjectStoragePolicy.IsKeyUnderPrefix("parcels/", key).Should().BeFalse();

    [Trait("Tier", "Fast")]
    [Fact]
    public void Key_OverTheS3LimitIsRefused()
        => ObjectStoragePolicy.IsKeyUnderPrefix(string.Empty, new string('k', ObjectStoragePolicy.MaxKeyBytes + 1)).Should().BeFalse();

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(null, "parcels/")]
    [InlineData("parcels/2026", "parcels/2026/")]
    public void BrowsePrefix_StaysUnderTheConnectionPrefix(string? requested, string expected)
    {
        ObjectStoragePolicy.ResolveBrowsePrefix("parcels/", requested).Should().Be(expected);
        ObjectStoragePolicy.ResolveBrowsePrefix("parcels/", "other/").Should().BeNull();
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void ExternalId_IsRandomAndHex()
    {
        var first = ObjectStoragePolicy.NewExternalId();
        var second = ObjectStoragePolicy.NewExternalId();

        first.Should().MatchRegex("^[0-9a-f]{64}$");
        second.Should().NotBe(first);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void SessionPolicy_IsReadOnlyOnTheBucketPrefix()
    {
        using var policy = JsonDocument.Parse(ObjectStorageSessionPolicy.Build("customer-imports", "parcels/"));

        var statements = policy.RootElement.GetProperty("Statement").EnumerateArray().ToArray();
        statements.Select(statement => statement.GetProperty("Effect").GetString()).Should().AllBe("Allow");
        var read = statements.Single(statement => statement.GetProperty("Action").GetString() == "s3:GetObject");
        read.GetProperty("Resource").GetString().Should().Be("arn:aws:s3:::customer-imports/parcels/*");
        var list = statements.Single(statement => statement.GetProperty("Action").GetString() == "s3:ListBucket");
        list.GetProperty("Resource").GetString().Should().Be("arn:aws:s3:::customer-imports");
        list.GetProperty("Condition").GetProperty("StringLike").GetProperty("s3:prefix")[0].GetString().Should().Be("parcels/*");
        statements.Select(statement => statement.GetProperty("Action").GetString())
            .Should().NotContain(action => action!.Contains("Put", StringComparison.Ordinal) || action.Contains("Delete", StringComparison.Ordinal));
        ObjectStorageSessionPolicy.DurationSeconds.Should().Be(900);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void SessionPolicy_RefusesUnvalidatedValues()
    {
        var act = () => ObjectStorageSessionPolicy.Build("customer-imports", "parcels/*");

        act.Should().Throw<ArgumentException>();
    }
}
