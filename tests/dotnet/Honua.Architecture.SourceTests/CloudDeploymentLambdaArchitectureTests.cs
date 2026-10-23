// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;

namespace Honua.Architecture.Tests;

/// <summary>
/// The unsuffixed Lambda AOT tag is the certified x86_64/amd64 image.
/// arm64 is only the explicit <c>-arm64</c> tag.
/// </summary>
public sealed class CloudDeploymentLambdaArchitectureTests
{
    [ArchitectureTest]
    public void Guide_UnsuffixedLambdaTag_IsX86AndArm64UsesExplicitTag()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var guide = File.ReadAllText(ArchitectureTestHelpers.CombinePath(
            root, "docs", "guides", "deploy", "cloud-deployments.md"));

        var tableRow = guide.Split('\n').Single(line =>
            line.StartsWith("| AWS Lambda |", StringComparison.Ordinal));
        var lambdaStart = guide.IndexOf("## AWS Lambda\n", StringComparison.Ordinal);
        lambdaStart.Should().BeGreaterThanOrEqualTo(0);
        var next = guide.IndexOf("\n## ", lambdaStart + 1, StringComparison.Ordinal);
        next.Should().BeGreaterThan(lambdaStart);
        var lambda = guide[lambdaStart..next];

        AssertUnsuffixedTagIsNotCalledArm64(guide);
        tableRow.Should().Contain("`*-lambda-aot` (single-arch `x86_64`/amd64)");
        lambda.Should().Contain("vX.Y.Z-lambda-aot-arm64");
        lambda.Should().Contain("the `x86_64` variant, which the unsuffixed `vX.Y.Z-lambda-aot` tag also resolves to");
        lambda.Should().Contain("architecture set to `x86_64`");
    }

    private static void AssertUnsuffixedTagIsNotCalledArm64(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var masked = line
                .Replace("-lambda-aot-arm64", "", StringComparison.Ordinal)
                .Replace("-lambda-aot-amd64", "", StringComparison.Ordinal);
            if (!MentionsUnsuffixedLambdaTag(masked))
            {
                continue;
            }

            masked.Should().NotContain("arm64",
                "the unsuffixed lambda-aot tag is x86_64/amd64; arm64 is the explicit -arm64 tag");
        }
    }

    private static bool MentionsUnsuffixedLambdaTag(string masked)
        => masked.Contains("*-lambda-aot", StringComparison.Ordinal)
            || masked.Contains("vX.Y.Z-lambda-aot", StringComparison.Ordinal)
            || masked.Contains("v1.2.3-lambda-aot", StringComparison.Ordinal);
}
