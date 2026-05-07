// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Enforces the release-train evidence manifest contract.
/// </summary>
[Trait("Category", "Architecture")]
public sealed class ReleaseTrainManifestTests
{
    private const string ManifestRelativePath = "release/honua-2026-05-preview.json";
    private const string SdkCompatibilityManifestRelativePath = "docs/developer/sdk-compatibility-versions.json";

    private static readonly string[] RequiredLaneIds =
    [
        "server-sdk-compatibility",
        "server-real-client-interop",
        "server-security-nightly",
        "server-license-activation",
        "sdk-js-trunk-ci",
        "sdk-js-quickstart-staging",
        "sdk-python-staging-integration",
        "mobile-dotnet-sdk-train",
        "helm-release-candidate-metadata",
        "admin-docs-supported-surfaces",
        "release-train-scoreboard",
        "release-candidate-image-compatibility",
        "release-evidence-pack",
        "license-marketplace-entitlements"
    ];

    [ArchitectureTest]
    public void Honua202605PreviewManifest_ShouldReferenceSharedSdkCompatibilityManifest()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();

        using var releaseManifest = LoadJson(repositoryRoot, ManifestRelativePath);
        using var sdkManifest = LoadJson(repositoryRoot, SdkCompatibilityManifestRelativePath);

        var releaseRoot = releaseManifest.RootElement;
        var sdkRoot = sdkManifest.RootElement;
        var compatibilityBaseline = releaseRoot.GetProperty("compatibilityBaseline");

        releaseRoot.GetProperty("releaseId").GetString().Should().Be("honua-2026-05-preview");
        releaseRoot.GetProperty("channel").GetString().Should().Be("preview");
        releaseRoot.GetProperty("sdkCompatibilityManifest").GetString().Should().Be(SdkCompatibilityManifestRelativePath);
        compatibilityBaseline.GetProperty("sourceOfTruth").GetString().Should().Be(SdkCompatibilityManifestRelativePath);
        compatibilityBaseline.GetProperty("adminApiMajor").GetString().Should().Be(sdkRoot.GetProperty("adminApiMajor").GetString());
        compatibilityBaseline.GetProperty("matrixDepth").GetInt32().Should().Be(sdkRoot.GetProperty("matrixDepth").GetInt32());
    }

    [ArchitectureTest]
    public void Honua202605PreviewManifest_ShouldCoverEveryReleaseReadinessLane()
    {
        using var releaseManifest = LoadJson(ArchitectureTestHelpers.ResolveRepositoryRoot(), ManifestRelativePath);

        var releaseRoot = releaseManifest.RootElement;
        var laneIds = EnumerateReleaseItems(releaseRoot)
            .Select(item => item.GetProperty("id").GetString())
            .ToArray();

        foreach (var requiredLaneId in RequiredLaneIds)
        {
            laneIds.Should().Contain(requiredLaneId);
        }
    }

    [ArchitectureTest]
    public void Honua202605PreviewManifest_BlockedLanesShouldHaveWaiverOrTraceableBlocker()
    {
        using var releaseManifest = LoadJson(ArchitectureTestHelpers.ResolveRepositoryRoot(), ManifestRelativePath);

        var releaseRoot = releaseManifest.RootElement;
        var missingChildTicketRepos = releaseRoot.GetProperty("missingBoundedChildTickets")
            .EnumerateArray()
            .Select(item => item.GetProperty("repo").GetString())
            .Where(repo => !string.IsNullOrWhiteSpace(repo))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var item in EnumerateReleaseItems(releaseRoot))
        {
            var itemId = item.GetProperty("id").GetString();
            var evidenceState = item.GetProperty("evidenceState").GetString();
            if (string.Equals(evidenceState, "passed", StringComparison.Ordinal))
            {
                continue;
            }

            var hasWaiver = HasApprovedWaiver(item);
            var hasBlockers = item.TryGetProperty("blockers", out var blockers)
                && blockers.ValueKind == JsonValueKind.Array
                && blockers.GetArrayLength() > 0;

            (hasWaiver || hasBlockers).Should().BeTrue(
                $"{itemId} is {evidenceState} and must have either an approved waiver or a traceable blocker");

            if (!hasBlockers)
            {
                continue;
            }

            foreach (var blocker in blockers.EnumerateArray())
            {
                blocker.GetProperty("repo").GetString().Should().NotBeNullOrWhiteSpace();
                blocker.GetProperty("reason").GetString().Should().NotBeNullOrWhiteSpace();

                if (GetNullableString(blocker, "number") is null)
                {
                    missingChildTicketRepos.Should().Contain(
                        blocker.GetProperty("repo").GetString(),
                        $"{itemId} has a blocker without a filed issue, so the missing bounded child ticket must be called out");
                }
            }
        }
    }

    [ArchitectureTest]
    public void Honua202605PreviewManifest_WaiversShouldRequireApprovalExpiryAndFollowUp()
    {
        using var releaseManifest = LoadJson(ArchitectureTestHelpers.ResolveRepositoryRoot(), ManifestRelativePath);

        var releaseRoot = releaseManifest.RootElement;
        foreach (var waiver in releaseRoot.GetProperty("waivers").EnumerateArray())
        {
            AssertApprovedWaiver(waiver);
        }

        foreach (var item in EnumerateReleaseItems(releaseRoot))
        {
            if (item.TryGetProperty("waiver", out var waiver) && waiver.ValueKind == JsonValueKind.Object)
            {
                AssertApprovedWaiver(waiver);
            }
        }
    }

    [ArchitectureTest]
    public void Honua202605PreviewManifest_ShouldNotClaimImageEvidenceWithoutDigest()
    {
        using var releaseManifest = LoadJson(ArchitectureTestHelpers.ResolveRepositoryRoot(), ManifestRelativePath);

        var image = releaseManifest.RootElement.GetProperty("candidate").GetProperty("image");
        var tag = GetNullableString(image, "tag");
        var digest = GetNullableString(image, "digest");
        var evidenceState = image.GetProperty("evidenceState").GetString();

        tag.Should().NotBe("latest", "release-candidate image evidence must not depend on mutable tags");

        if (string.Equals(evidenceState, "validated", StringComparison.Ordinal))
        {
            digest.Should().NotBeNullOrWhiteSpace();
            digest.Should().StartWith("sha256:", "image-backed release evidence must name the immutable digest");
            return;
        }

        image.GetProperty("blockers").GetArrayLength().Should().BeGreaterThan(
            0,
            "non-validated image evidence must name the bounded blocker");
    }

    private static JsonDocument LoadJson(string repositoryRoot, string relativePath)
    {
        var path = ResolvePath(repositoryRoot, relativePath);

        File.Exists(path).Should().BeTrue($"{relativePath} must exist");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static string ResolvePath(string repositoryRoot, string relativePath)
    {
        return Path.Combine(repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static IEnumerable<JsonElement> EnumerateReleaseItems(JsonElement root)
    {
        foreach (var propertyName in new[] { "releaseGates", "repositoryLanes", "releaseLaneCriteria" })
        {
            foreach (var item in root.GetProperty(propertyName).EnumerateArray())
            {
                yield return item;
            }
        }
    }

    private static bool HasApprovedWaiver(JsonElement item)
    {
        return item.TryGetProperty("waiver", out var waiver)
            && waiver.ValueKind == JsonValueKind.Object
            && IsApprovedWaiver(waiver);
    }

    private static void AssertApprovedWaiver(JsonElement waiver)
    {
        IsApprovedWaiver(waiver).Should().BeTrue("release waivers must include approval, expiry, and follow-up evidence");
    }

    private static bool IsApprovedWaiver(JsonElement waiver)
    {
        return !string.IsNullOrWhiteSpace(GetNullableString(waiver, "approver"))
            && !string.IsNullOrWhiteSpace(GetNullableString(waiver, "expiresOn"))
            && waiver.TryGetProperty("followUpIssue", out var followUpIssue)
            && followUpIssue.ValueKind == JsonValueKind.Object
            && !string.IsNullOrWhiteSpace(GetNullableString(followUpIssue, "repo"))
            && GetNullableString(followUpIssue, "number") is not null
            && !string.IsNullOrWhiteSpace(GetNullableString(waiver, "reason"));
    }

    private static string? GetNullableString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null
        };
    }
}
