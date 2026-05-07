// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;

namespace Honua.Core.Tests.Features.AiBuilder;

public sealed class AiBuilderContractFixtureTests
{
    private const string FixtureVersion = "honua.ai_builder.spatial_query.v1";

    [Fact]
    public async Task SpatialQueryFixture_CoversRequiredOutcomeCases()
    {
        using var document = await LoadFixtureAsync();
        var root = document.RootElement;

        root.GetProperty("contractVersion").GetString().Should().Be(FixtureVersion);
        root.GetProperty("fixtureProfile").GetString().Should().Be("deterministic-no-model");

        var cases = root.GetProperty("scenarios")
            .EnumerateArray()
            .Select(scenario => scenario.GetProperty("case").GetString())
            .ToHashSet(StringComparer.Ordinal);

        cases.Should().BeEquivalentTo(
            "success",
            "ambiguity",
            "unsupported",
            "auth-denied",
            "oversized",
            "cache-hit",
            "apply-failure");
    }

    [Fact]
    public async Task CapabilityDiscovery_AdvertisesMetadataPredicatesTemplatesAndMcpInspection()
    {
        using var document = await LoadFixtureAsync();
        var discovery = document.RootElement.GetProperty("capabilityDiscovery");

        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in discovery.GetProperty("sources").EnumerateArray())
        {
            var sourceId = source.GetProperty("sourceId").GetString();
            sourceId.Should().NotBeNullOrWhiteSpace();
            sourceIds.Add(sourceId!).Should().BeTrue();
            source.GetProperty("fields").GetArrayLength().Should().BeGreaterThan(0);
            source.GetProperty("geometryColumn").GetString().Should().NotBeNullOrWhiteSpace();
            source.GetProperty("crs").GetString().Should().StartWith("EPSG:");
        }

        var predicates = discovery.GetProperty("spatialQueryCapabilities")
            .EnumerateArray()
            .ToDictionary(
                item => item.GetProperty("name").GetString()!,
                item => item.GetProperty("state").GetString()!,
                StringComparer.Ordinal);

        predicates.Should().ContainKeys(
            "bbox",
            "intersects",
            "contains",
            "within",
            "withinDistance",
            "attributeFilter",
            "groupingAggregation",
            "spatialJoin");
        predicates.Values.Should().OnlyContain(state =>
            state == "supported" || state == "degraded" || state == "unsupported");

        var templates = discovery.GetProperty("starterOutputTemplates")
            .EnumerateArray()
            .Select(template => template.GetProperty("templateId").GetString())
            .ToHashSet(StringComparer.Ordinal);

        templates.Should().BeEquivalentTo(
            "map-only",
            "map-plus-table",
            "map-plus-chart",
            "filtered-map",
            "linked-dashboard");

        var mcp = discovery.GetProperty("mcpInspection");
        mcp.GetProperty("tools").EnumerateArray().Select(tool => tool.GetString())
            .Should().Contain(["honua_ground_candidates", "honua_clarify_intent", "honua_execute_plan"]);
        mcp.GetProperty("resources").EnumerateArray().Select(resource => resource.GetString())
            .Should().Contain(["honua://jobs/{jobId}/results", "honua://map-packages/{packageId}", "honua://app-packages/{packageId}"]);
    }

    [Fact]
    public async Task SuccessAndCacheHitScenarios_SurfaceDraftPlanWarningsCacheAndPackageArtifacts()
    {
        using var document = await LoadFixtureAsync();

        var success = FindScenario(document.RootElement, "success-linked-dashboard");
        success.GetProperty("draft").GetProperty("filterPlan").GetProperty("clauses").GetArrayLength().Should().BeGreaterThan(0);
        success.GetProperty("draft").GetProperty("specDraft").GetProperty("grammarVersion").GetString().Should().Be("v1.0");
        success.GetProperty("draft").GetProperty("appDraft").GetProperty("targetSdk").GetString().Should().Be("honua-sdk-js");

        var plan = success.GetProperty("plan");
        plan.GetProperty("dag").GetArrayLength().Should().BeGreaterThan(1);
        plan.GetProperty("warnings").EnumerateArray()
            .Should().Contain(warning => warning.GetProperty("code").GetString() == "mutable_source_cache_warning");
        plan.GetProperty("cache").GetProperty("hit").GetBoolean().Should().BeFalse();

        AssertPackageArtifacts(success.GetProperty("apply"));

        var cacheHit = FindScenario(document.RootElement, "cache-hit-reused-packages");
        cacheHit.GetProperty("plan").GetProperty("cache").GetProperty("hit").GetBoolean().Should().BeTrue();
        cacheHit.GetProperty("plan").GetProperty("warnings").EnumerateArray()
            .Should().Contain(warning => warning.GetProperty("code").GetString() == "cache_hit");
        AssertPackageArtifacts(cacheHit.GetProperty("apply"));
    }

    [Fact]
    public async Task FailureAndClarificationScenarios_UseStructuredRecoverableShapes()
    {
        using var document = await LoadFixtureAsync();

        var discoveredSources = document.RootElement.GetProperty("capabilityDiscovery")
            .GetProperty("sources")
            .EnumerateArray()
            .Select(source => source.GetProperty("sourceId").GetString())
            .OfType<string>()
            .Where(sourceId => !string.IsNullOrWhiteSpace(sourceId))
            .ToHashSet(StringComparer.Ordinal);

        var ambiguity = FindScenario(document.RootElement, "ambiguity-source-and-unit");
        ambiguity.GetProperty("draft").GetProperty("status").GetString().Should().Be("clarification_required");
        var candidates = ambiguity.GetProperty("clarification").GetProperty("candidates")
            .EnumerateArray()
            .ToArray();
        var candidateKinds = candidates
            .Select(candidate => candidate.GetProperty("kind").GetString())
            .ToHashSet(StringComparer.Ordinal);
        candidateKinds.Should().Contain(["source", "unit", "operation"]);
        var sourceOptions = candidates
            .Single(candidate => candidate.GetProperty("kind").GetString() == "source")
            .GetProperty("options")
            .EnumerateArray()
            .Select(option => option.GetString()!)
            .ToArray();
        sourceOptions.Should().OnlyContain(sourceId => discoveredSources.Contains(sourceId));

        var unsupported = FindScenario(document.RootElement, "unsupported-spatial-join");
        unsupported.GetProperty("capabilityState").GetProperty("state").GetString().Should().Be("unsupported");
        unsupported.GetProperty("warnings").EnumerateArray()
            .Should().Contain(warning => warning.GetProperty("code").GetString() == "unsupported_capability");

        var authDenied = FindScenario(document.RootElement, "auth-denied-private-source");
        authDenied.GetProperty("error").GetProperty("code").GetString().Should().Be("auth_denied");
        authDenied.GetProperty("error").TryGetProperty("policyRef", out _).Should().BeTrue();

        var oversized = FindScenario(document.RootElement, "oversized-estimate");
        oversized.GetProperty("plan").GetProperty("status").GetString().Should().Be("rejected");
        oversized.GetProperty("plan").GetProperty("estimate").GetProperty("featureCount").GetInt64()
            .Should().BeGreaterThan(oversized.GetProperty("plan").GetProperty("estimate").GetProperty("limit").GetInt64());

        var applyFailure = FindScenario(document.RootElement, "apply-failure-package-step");
        applyFailure.GetProperty("apply").GetProperty("status").GetString().Should().Be("failed");
        applyFailure.GetProperty("apply").GetProperty("job").GetProperty("resourceUri").GetString()
            .Should().StartWith("honua://jobs/");
        applyFailure.GetProperty("apply").GetProperty("warnings").EnumerateArray()
            .Should().Contain(warning => warning.GetProperty("code").GetString() == "artifact_write_failed");
    }

    private static void AssertPackageArtifacts(JsonElement apply)
    {
        apply.GetProperty("status").GetString().Should().Be("succeeded");
        var artifacts = apply.GetProperty("artifacts").EnumerateArray().ToList();

        artifacts.Should().Contain(artifact =>
            artifact.GetProperty("kind").GetString() == "MapPackage"
            && artifact.GetProperty("format").GetString() == "honua_map_package.v1"
            && artifact.GetProperty("resourceUri").GetString()!.StartsWith("honua://map-packages/", StringComparison.Ordinal)
            && artifact.GetProperty("packageable").GetBoolean());

        artifacts.Should().Contain(artifact =>
            artifact.GetProperty("kind").GetString() == "AppPackage"
            && artifact.GetProperty("format").GetString() == "honua_app_package.v1"
            && artifact.GetProperty("resourceUri").GetString()!.StartsWith("honua://app-packages/", StringComparison.Ordinal)
            && artifact.GetProperty("packageable").GetBoolean());
    }

    private static JsonElement FindScenario(JsonElement root, string id) =>
        root.GetProperty("scenarios")
            .EnumerateArray()
            .Single(scenario => scenario.GetProperty("id").GetString() == id);

    private static async Task<JsonDocument> LoadFixtureAsync()
    {
        await using var stream = File.OpenRead(ResolveRepoPath(
            Path.Combine("tests", "fixtures", "ai-builder", "spatial-query-contract-v1.json")));

        return await JsonDocument.ParseAsync(stream);
    }

    private static string ResolveRepoPath(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Unable to locate '{relativePath}' from '{AppContext.BaseDirectory}'.");
    }
}
