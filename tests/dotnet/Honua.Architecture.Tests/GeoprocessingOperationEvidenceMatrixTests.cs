// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Geoprocessing;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Enforces the whole-catalog geoprocessing execution-evidence denominator adopted
/// for 2026.1. A catalog addition cannot land without an explicit evidence verdict.
/// </summary>
[Trait("Category", "Architecture")]
public sealed class GeoprocessingOperationEvidenceMatrixTests
{
    private const string ManifestRelativePath = "certification/gp-operation-matrix.v1.json";

    [Fact]
    public void ManifestProcessIds_ExactlyMatchBuiltInCatalog()
    {
        using var manifest = ReadManifest();
        var manifestIds = manifest.RootElement.GetProperty("operations")
            .EnumerateArray()
            .Select(row => row.GetProperty("processId").GetString())
            .ToList();
        var catalogIds = new BuiltInProcessCatalog().ListProcesses()
            .Select(process => process.ProcessId)
            .ToList();

        manifestIds.Should().NotContainNulls();
        manifestIds.Should().OnlyHaveUniqueItems("each catalog process must have exactly one matrix verdict");
        manifestIds.Should().BeEquivalentTo(
            catalogIds,
            "a BuiltInProcessCatalog addition or removal must update the committed per-operation matrix");
    }

    [Fact]
    public void ManifestRows_HaveAuditableEvidenceOrOneConcreteGapIssue()
    {
        using var manifest = ReadManifest();
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var issueUrls = new List<string>();

        foreach (var row in manifest.RootElement.GetProperty("operations").EnumerateArray())
        {
            var processId = RequiredString(row, "processId");
            var status = RequiredString(row, "status");
            status.Should().BeOneOf("proven", "partially-proven", "unproven");

            var evidence = row.GetProperty("evidence").EnumerateArray().ToList();
            if (status == "proven")
            {
                evidence.Should().NotBeEmpty($"proven operation '{processId}' needs execution-content evidence");
                row.TryGetProperty("gap", out _).Should().BeFalse(
                    $"proven operation '{processId}' cannot retain an unresolved gap");
            }
            else
            {
                if (status == "partially-proven")
                {
                    evidence.Should().NotBeEmpty(
                        $"partially-proven operation '{processId}' must identify the evidence that falls short");
                }
                else
                {
                    evidence.Should().BeEmpty($"unproven operation '{processId}' cannot claim execution evidence");
                }

                row.TryGetProperty("gap", out var gap).Should().BeTrue(
                    $"{status} operation '{processId}' needs one concrete follow-up issue");
                RequiredString(gap, "missing").Should().NotBeNullOrWhiteSpace();
                var issue = RequiredString(gap, "issue");
                issue.Should().MatchRegex(@"^https://github\.com/honua-io/honua-server/issues/\d+$");
                issueUrls.Add(issue);
            }

            foreach (var receipt in evidence)
            {
                var relativePath = RequiredString(receipt, "path");
                var testName = RequiredString(receipt, "test");
                RequiredString(receipt, "assertion").Should().NotBeNullOrWhiteSpace();

                var evidencePath = ArchitectureTestHelpers.CombinePath(
                    repositoryRoot,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
                File.Exists(evidencePath).Should().BeTrue(
                    $"evidence path for '{processId}' should exist: {relativePath}");
                File.ReadAllText(evidencePath).Should().Contain(
                    testName,
                    $"evidence test for '{processId}' should remain present in {relativePath}");
            }
        }

        issueUrls.Should().OnlyHaveUniqueItems(
            "partial and unproven operations require one-issue-per-operation follow-up");
    }

    [Fact]
    public void ManifestSummaryAndSharedRuntimeReferences_MatchRows()
    {
        using var manifest = ReadManifest();
        var root = manifest.RootElement;
        root.GetProperty("schemaVersion").GetInt32().Should().Be(1);
        RequiredString(root, "catalogVersion").Should().Be(BuiltInProcessCatalog.CatalogVersion);

        var operations = root.GetProperty("operations").EnumerateArray().ToList();
        var summary = root.GetProperty("summary");
        summary.GetProperty("total").GetInt32().Should().Be(operations.Count);
        var byStatus = summary.GetProperty("byStatus");
        foreach (var status in new[] { "proven", "partially-proven", "unproven" })
        {
            byStatus.GetProperty(status).GetInt32().Should().Be(
                operations.Count(row => RequiredString(row, "status") == status));
        }

        var sharedRuntimeGaps = root.GetProperty("sharedRuntimeGaps")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToList();
        sharedRuntimeGaps.Should().Equal(
            Enumerable.Range(3848, 10)
                .Select(number => $"https://github.com/honua-io/honua-server/issues/{number}"));
    }

    private static JsonDocument ReadManifest()
    {
        var manifestPath = ArchitectureTestHelpers.CombinePath(
            ArchitectureTestHelpers.ResolveRepositoryRoot(),
            ManifestRelativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(manifestPath).Should().BeTrue(
            $"the whole-catalog GP execution matrix should exist at {ManifestRelativePath}");

        return JsonDocument.Parse(File.ReadAllText(manifestPath));
    }

    private static string RequiredString(JsonElement element, string propertyName)
    {
        element.TryGetProperty(propertyName, out var property).Should().BeTrue(
            $"matrix property '{propertyName}' is required");
        var value = property.GetString();
        value.Should().NotBeNullOrWhiteSpace($"matrix property '{propertyName}' is required");
        return value!;
    }
}
