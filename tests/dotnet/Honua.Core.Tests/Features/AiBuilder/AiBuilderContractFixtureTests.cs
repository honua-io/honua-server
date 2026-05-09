// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;

namespace Honua.Core.Tests.Features.AiBuilder;

public sealed class AiBuilderContractFixtureTests
{
    private const string FixtureVersion = "honua.ai_builder.spatial_query.v1";
    private const string OperationsDashboardFixtureVersion = "honua.ai_builder.operations_dashboard.v1";
    private const string OperationsDashboardFixtureFileName = "operations-dashboard-contract-v1.json";
    private const string OperationsDashboardProofPrompt =
        "Build an operations dashboard for this saved map showing a map, incident list, incident count, incidents by type chart, and district filter.";

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
    public async Task OperationsDashboardFixture_RunsWithoutModelCallsAndCoversRequiredOutcomeCases()
    {
        using var document = await LoadFixtureAsync(OperationsDashboardFixtureFileName);
        var root = document.RootElement;

        root.GetProperty("contractVersion").GetString().Should().Be(OperationsDashboardFixtureVersion);
        root.GetProperty("fixtureProfile").GetString().Should().Be("deterministic-no-model");
        root.GetProperty("proofPrompt").GetString().Should().Be(OperationsDashboardProofPrompt);
        root.GetProperty("modelInvocation").GetProperty("mode").GetString().Should().Be("disabled");
        root.GetProperty("modelInvocation").GetProperty("allowed").GetBoolean().Should().BeFalse();

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
    public async Task OperationsDashboardSuccess_ReturnsDraftSpecPlanProgressAndManifestPackageArtifacts()
    {
        using var document = await LoadFixtureAsync(OperationsDashboardFixtureFileName);
        var success = FindScenario(document.RootElement, "success-operations-dashboard");

        success.GetProperty("prompt").GetString().Should().Be(OperationsDashboardProofPrompt);
        var draft = success.GetProperty("draft");
        draft.GetProperty("status").GetString().Should().Be("ready");

        var widgetIds = draft.GetProperty("structuredDraft")
            .GetProperty("widgets")
            .EnumerateArray()
            .Select(widget => widget.GetProperty("id").GetString())
            .ToHashSet(StringComparer.Ordinal);
        widgetIds.Should().BeEquivalentTo(
            "map",
            "incident-list",
            "incident-count",
            "incidents-by-type",
            "district-filter");

        var specDraft = draft.GetProperty("specDraft");
        specDraft.GetProperty("reviewStatus").GetString().Should().Be("reviewable");
        specDraft.GetProperty("specKind").GetString().Should().Be("CanonicalSpecDocument");
        var canonicalSpec = specDraft.GetProperty("canonicalSpecDocument");
        canonicalSpec.GetProperty("grammarVersion").GetString().Should().Be("v1.0");
        canonicalSpec.GetProperty("processFamilyVersion").GetString().Should().Be("ai-builder.operations-dashboard.v1");
        canonicalSpec.GetProperty("nodes").EnumerateArray()
            .Should().Contain(node => node.GetProperty("kind").GetString() == "App");

        var appDraft = draft.GetProperty("appDraft");
        appDraft.GetProperty("targetSdk").GetString().Should().Be("honua-sdk-js");
        appDraft.GetProperty("runtimeConfigSchema").GetProperty("required").EnumerateArray()
            .Select(item => item.GetString())
            .Should().Contain(["honuaBaseUrl", "mapPackageUri", "appPackageUri"]);

        var plan = success.GetProperty("plan");
        plan.GetProperty("dag").EnumerateArray()
            .Should().Contain(node => node.GetProperty("nodeId").GetString() == "emit-app-manifest");
        plan.GetProperty("warnings").EnumerateArray()
            .Should().Contain(warning => warning.GetProperty("code").GetString() == "mutable_source_cache_warning");
        plan.GetProperty("cache").GetProperty("hit").GetBoolean().Should().BeFalse();

        var apply = success.GetProperty("apply");
        AssertPackageArtifacts(apply);
        var progress = apply.GetProperty("job").GetProperty("progress");
        progress.GetProperty("percentComplete").GetInt32().Should().Be(100);
        progress.GetProperty("events").EnumerateArray()
            .Should().Contain(e => e.GetProperty("stage").GetString() == "emit-app-manifest");

        AssertOperationsDashboardAppPackage(apply);
    }

    [Fact]
    public async Task OperationsDashboardEdgeScenarios_UseStructuredRecoverableShapes()
    {
        using var document = await LoadFixtureAsync(OperationsDashboardFixtureFileName);

        var ambiguity = FindScenario(document.RootElement, "ambiguity-source-field-geometry-crs-predicate-aggregation");
        ambiguity.GetProperty("draft").GetProperty("status").GetString().Should().Be("clarification_required");
        var clarificationKinds = ambiguity.GetProperty("clarification")
            .GetProperty("candidates")
            .EnumerateArray()
            .Select(candidate => candidate.GetProperty("kind").GetString())
            .ToHashSet(StringComparer.Ordinal);
        clarificationKinds.Should().Contain(["source", "field", "geometry", "crs", "predicate", "aggregation"]);

        var unsupported = FindScenario(document.RootElement, "unsupported-kernel-density");
        unsupported.GetProperty("capabilityState").GetProperty("state").GetString().Should().Be("unsupported");
        unsupported.GetProperty("warnings").EnumerateArray()
            .Should().Contain(warning => warning.GetProperty("code").GetString() == "unsupported_capability");

        var authDenied = FindScenario(document.RootElement, "rbac-denied-restricted-incidents");
        authDenied.GetProperty("error").GetProperty("code").GetString().Should().Be("auth_denied");
        authDenied.GetProperty("error").GetProperty("policyRef").GetString()
            .Should().StartWith("rbac:");

        var oversized = FindScenario(document.RootElement, "oversized-estimate-history");
        oversized.GetProperty("plan").GetProperty("status").GetString().Should().Be("rejected");
        oversized.GetProperty("plan").GetProperty("estimate").GetProperty("featureCount").GetInt64()
            .Should().BeGreaterThan(oversized.GetProperty("plan").GetProperty("estimate").GetProperty("limit").GetInt64());

        var cacheHit = FindScenario(document.RootElement, "cache-hit-reused-operations-dashboard");
        cacheHit.GetProperty("plan").GetProperty("cache").GetProperty("hit").GetBoolean().Should().BeTrue();
        cacheHit.GetProperty("plan").GetProperty("warnings").EnumerateArray()
            .Should().Contain(warning => warning.GetProperty("code").GetString() == "cache_hit");
        cacheHit.GetProperty("apply").GetProperty("artifacts").EnumerateArray()
            .Should().Contain(artifact => artifact.GetProperty("kind").GetString() == "AppManifest");

        var applyFailure = FindScenario(document.RootElement, "apply-failure-manifest-write");
        applyFailure.GetProperty("apply").GetProperty("status").GetString().Should().Be("failed");
        applyFailure.GetProperty("apply").GetProperty("job").GetProperty("progress").GetProperty("phase").GetString()
            .Should().Be("emit-app-manifest");
        applyFailure.GetProperty("apply").GetProperty("warnings").EnumerateArray()
            .Should().Contain(warning =>
                warning.GetProperty("code").GetString() == "artifact_write_failed" &&
                warning.GetProperty("artifactKind").GetString() == "AppManifest" &&
                warning.GetProperty("retryable").GetBoolean());
    }

    [Fact]
    public async Task OperationsDashboardMcpInspection_CoversBuilderResourceCategoriesAndSchemaRefs()
    {
        using var document = await LoadFixtureAsync(OperationsDashboardFixtureFileName);
        var discovery = document.RootElement.GetProperty("capabilityDiscovery");
        var mcp = discovery.GetProperty("mcpInspection");

        mcp.GetProperty("tools").EnumerateArray().Select(tool => tool.GetString())
            .Should().Contain([
                "honua_ground_candidates",
                "honua_clarify_intent",
                "honua_validate_plan",
                "honua_dry_run_plan",
                "honua_execute_plan"]);

        var resources = mcp.GetProperty("resources").EnumerateArray().ToArray();
        resources.Select(resource => resource.GetProperty("category").GetString())
            .ToHashSet(StringComparer.Ordinal)
            .Should().Contain([
                "services",
                "schemas",
                "processes",
                "packages",
                "artifacts",
                "jobs",
                "deployments"]);

        var schemaIds = discovery.GetProperty("schemaPreviews")
            .EnumerateArray()
            .Select(schema => schema.GetProperty("schemaId").GetString())
            .ToHashSet(StringComparer.Ordinal);
        foreach (var schemaResource in resources.Where(resource => resource.GetProperty("category").GetString() == "schemas"))
        {
            schemaResource.TryGetProperty("schemaRef", out var schemaRef).Should().BeTrue();
            schemaIds.Should().Contain(schemaRef.GetString());
        }

        resources.Select(resource => resource.GetProperty("uri").GetString()).Should().Contain([
            "honua://published-services/city-ops-incidents",
            "honua://catalog/processes",
            "honua://map-packages/map-pkg-ops-dashboard",
            "honua://app-packages/app-pkg-ops-dashboard",
            "honua://jobs/job-ops-dashboard-success",
            "honua://jobs/job-ops-dashboard-success/results",
            "honua://deployments/deploy-ops-dashboard"]);
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

    private static void AssertOperationsDashboardAppPackage(JsonElement apply)
    {
        var appPackage = apply.GetProperty("packages").GetProperty("appPackage");
        appPackage.GetProperty("appPackageId").GetString().Should().Be("app-pkg-ops-dashboard");
        appPackage.GetProperty("targetSdk").GetString().Should().Be("honua-sdk-js");
        appPackage.GetProperty("mapPackageId").GetString().Should().Be("map-pkg-ops-dashboard");
        appPackage.GetProperty("manifestArtifactId").GetString().Should().Be("artifact-app-manifest-ops");
        appPackage.GetProperty("bundleArtifactId").GetString().Should().Be("artifact-app-bundle-ops");

        var manifestArtifact = appPackage.GetProperty("manifestArtifact");
        manifestArtifact.GetProperty("artifactId").GetString().Should().Be("artifact-app-manifest-ops");
        manifestArtifact.GetProperty("format").GetString().Should().Be("honua_app_manifest.v1");
        manifestArtifact.GetProperty("contentType").GetString().Should().Be("application/vnd.honua.app-manifest+json");
        manifestArtifact.GetProperty("resourceUri").GetString()
            .Should().Be("honua://jobs/job-ops-dashboard-success/results#artifact-app-manifest-ops");

        appPackage.GetProperty("generatedFiles").EnumerateArray().Select(file => file.GetString())
            .Should().Contain("src/honua-app-manifest.json");
        appPackage.GetProperty("assetManifest").EnumerateArray()
            .Should().Contain(asset =>
                asset.GetProperty("path").GetString() == "src/honua-app-manifest.json" &&
                asset.GetProperty("contentType").GetString() == "application/vnd.honua.app-manifest+json");
        appPackage.GetProperty("boundArtifacts").EnumerateArray().Select(artifact => artifact.GetString())
            .Should().Contain(["artifact-app-bundle-ops", "artifact-app-manifest-ops"]);

        var manifestPreview = appPackage.GetProperty("manifestPreview");
        manifestPreview.GetProperty("schemaVersion").GetString().Should().Be("honua.app_manifest.v1");
        manifestPreview.GetProperty("targetSdk").GetString().Should().Be("honua-sdk-js");
        manifestPreview.GetProperty("widgets").EnumerateArray().Select(widget => widget.GetString())
            .Should().Contain(["map", "incident-list", "incident-count", "incidents-by-type", "district-filter"]);
    }

    private static JsonElement FindScenario(JsonElement root, string id) =>
        root.GetProperty("scenarios")
            .EnumerateArray()
            .Single(scenario => scenario.GetProperty("id").GetString() == id);

    private static async Task<JsonDocument> LoadFixtureAsync(string fileName = "spatial-query-contract-v1.json")
    {
        await using var stream = File.OpenRead(ResolveRepoPath(
            Path.Combine("tests", "fixtures", "ai-builder", fileName)));

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
