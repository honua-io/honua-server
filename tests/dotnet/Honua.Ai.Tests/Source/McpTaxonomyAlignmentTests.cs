// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Deployment.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Grounding.Abstractions;
using Honua.Core.Features.Publishing.Abstractions;
using Honua.Geoprocessing;
using Honua.Server.Features.Protocols.Mcp;
using Honua.Server.Features.Protocols.Mcp.Resources;
using Honua.Server.Features.Protocols.Mcp.Tools;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Honua.Core.Features.Reporting.Abstractions;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Pins the MCP tool names, resource URIs, and workflow-family tags to the
/// geospatial-mcp taxonomy v1 as described in <c>AI_OPERATOR_CONTRACT.md §MCP
/// Contract Families</c>. A rename on either side trips this test.
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class McpTaxonomyAlignmentTests
{
    private static readonly string[] TaxonomyToolNames =
    {
        "honua_validate_plan",
        "honua_execute_plan",
        "honua_dry_run_plan",
        "honua_cancel_job",
        "honua_plan_analysis",
        "honua_ground_candidates",
        "honua_clarify_intent"
    };

    private static readonly string[] TaxonomyResourceUris =
    {
        "honua://jobs/{jobId}",
        "honua://jobs/{jobId}/results",
        "honua://jobs/{jobId}/report",
        "honua://workspaces/{workspaceId}",
        "honua://catalog/processes",
        "honua://published-services",
        "honua://published-services/{serviceId}",
        "honua://deployments",
        "honua://deployments/{deploymentId}",
        "honua://map-packages",
        "honua://map-packages/{packageId}",
        "honua://app-packages",
        "honua://app-packages/{packageId}"
    };

    [UnitTest]
    public void ToolNames_MatchTaxonomyRoster()
    {
        var tools = BuildTools();
        var names = tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        names.Should().BeEquivalentTo(TaxonomyToolNames.OrderBy(n => n, StringComparer.Ordinal));
    }

    [UnitTest]
    public void ToolNames_UseHonuaPrefix()
    {
        var tools = BuildTools();

        tools.Select(t => t.Name).Should().OnlyContain(n => n.StartsWith("honua_", StringComparison.Ordinal));
    }

    [UnitTest]
    public void WorkflowFamilies_MatchTelemetryVocabulary()
    {
        var tools = BuildTools();

        var families = new HashSet<string>(tools.Select(t => t.WorkflowFamily), StringComparer.Ordinal);
        families.Should().BeSubsetOf(new[]
        {
            McpTelemetry.WorkflowFamily.Planning,
            McpTelemetry.WorkflowFamily.Execution,
            McpTelemetry.WorkflowFamily.Lifecycle,
            McpTelemetry.WorkflowFamily.Results
        });
    }

    [UnitTest]
    public void FunctionalTools_BindToCorrectWorkflowFamily()
    {
        var jobService = Substitute.For<IGeoprocessingJobService>();

        new ValidatePlanTool(jobService, NullLogger<ValidatePlanTool>.Instance)
            .WorkflowFamily.Should().Be(McpTelemetry.WorkflowFamily.Planning);
        new DryRunPlanTool(jobService, NullLogger<DryRunPlanTool>.Instance)
            .WorkflowFamily.Should().Be(McpTelemetry.WorkflowFamily.Planning);
        new ExecutePlanTool(jobService, NullLogger<ExecutePlanTool>.Instance)
            .WorkflowFamily.Should().Be(McpTelemetry.WorkflowFamily.Execution);
        new CancelJobTool(jobService, NullLogger<CancelJobTool>.Instance)
            .WorkflowFamily.Should().Be(McpTelemetry.WorkflowFamily.Lifecycle);
    }

    [UnitTest]
    public void ResourceDescriptors_MatchTaxonomyUriTemplates()
    {
        var resources = BuildResources();

        // Parameterized URIs live on `resources/templates/list`; concrete URIs
        // stay on `resources/list`. The taxonomy union covers both surfaces.
        var uris = resources.SelectMany(r => r.Describe()).Select(d => d.Uri)
            .Concat(resources.SelectMany(r => r.DescribeTemplates()).Select(d => d.UriTemplate))
            .OrderBy(u => u, StringComparer.Ordinal)
            .ToArray();

        uris.Should().BeEquivalentTo(TaxonomyResourceUris.OrderBy(u => u, StringComparer.Ordinal));
    }

    [UnitTest]
    public void ResourceFamilies_MatchTelemetryVocabulary()
    {
        var resources = BuildResources();

        var families = new HashSet<string>(resources.Select(r => r.Family), StringComparer.Ordinal);
        families.Should().BeEquivalentTo(new[]
        {
            McpTelemetry.ResourceFamily.Jobs,
            McpTelemetry.ResourceFamily.JobResults,
            McpTelemetry.ResourceFamily.JobReports,
            McpTelemetry.ResourceFamily.Workspaces,
            McpTelemetry.ResourceFamily.Catalog,
            McpTelemetry.ResourceFamily.PublishedServices,
            McpTelemetry.ResourceFamily.Deployments,
            McpTelemetry.ResourceFamily.MapPackages,
            McpTelemetry.ResourceFamily.AppPackages,
            McpTelemetry.ResourceFamily.PromotionIndex
        });
    }

    [UnitTest]
    public void ResourceUris_UseHonuaScheme()
    {
        var resources = BuildResources();

        var concreteUris = resources.SelectMany(r => r.Describe()).Select(d => d.Uri);
        var templateUris = resources.SelectMany(r => r.DescribeTemplates()).Select(d => d.UriTemplate);

        concreteUris.Concat(templateUris)
            .Should().OnlyContain(u => u.StartsWith(McpResourceUris.Scheme + "://", StringComparison.Ordinal));
    }

    [UnitTest]
    public void ResourceDescriptors_AllAdvertiseJsonMimeType()
    {
        var resources = BuildResources();

        var concreteMimeTypes = resources.SelectMany(r => r.Describe()).Select(d => d.MimeType);
        var templateMimeTypes = resources.SelectMany(r => r.DescribeTemplates()).Select(d => d.MimeType);

        concreteMimeTypes.Concat(templateMimeTypes)
            .Should().OnlyContain(m => string.Equals(m, "application/json", StringComparison.Ordinal));
    }

    [UnitTest]
    public void ToolDescriptors_HaveNonEmptyDescriptions()
    {
        var tools = BuildTools();

        foreach (var tool in tools)
        {
            var descriptor = tool.Describe();
            descriptor.Name.Should().Be(tool.Name);
            descriptor.Description.Should().NotBeNullOrWhiteSpace();
        }
    }

    [UnitTest]
    public void PlanSchema_StepKindEnum_MatchesAnalysisPlanStepKind()
    {
        var stepKindEnum = ExtractEnumValues(McpToolSchemas.PlanArgumentSchema,
            "properties", "plan", "properties", "steps", "items", "properties", "kind", "enum");

        stepKindEnum.Should().BeEquivalentTo(Enum.GetNames<AnalysisPlanStepKind>());
    }

    [UnitTest]
    public void PlanSchema_OutputsEnum_MatchesArtifactKind()
    {
        var outputsEnum = ExtractEnumValues(McpToolSchemas.PlanArgumentSchema,
            "properties", "plan", "properties", "outputs", "items", "enum");

        outputsEnum.Should().BeEquivalentTo(Enum.GetNames<ArtifactKind>());
    }

    [UnitTest]
    public void ExecutePlanSchema_StepKindEnum_MatchesAnalysisPlanStepKind()
    {
        var stepKindEnum = ExtractEnumValues(McpToolSchemas.ExecutePlanArgumentSchema,
            "properties", "plan", "properties", "steps", "items", "properties", "kind", "enum");

        stepKindEnum.Should().BeEquivalentTo(Enum.GetNames<AnalysisPlanStepKind>());
    }

    [UnitTest]
    public void ExecutePlanSchema_OutputsEnum_MatchesArtifactKind()
    {
        var outputsEnum = ExtractEnumValues(McpToolSchemas.ExecutePlanArgumentSchema,
            "properties", "plan", "properties", "outputs", "items", "enum");

        outputsEnum.Should().BeEquivalentTo(Enum.GetNames<ArtifactKind>());
    }

    [UnitTest]
    public void PlanSchema_DoesNotRequirePlanIdOrSteps_SoValidatorCanReportViolations()
    {
        // validate_plan / dry_run_plan deliberately accept partial plans so the
        // runtime can report EMPTY_PLAN_ID and EMPTY_STEPS as structured
        // violations. Marking these fields required in the published schema
        // would cause strict JSON-schema clients to block inputs the validator
        // is meant to inspect.
        var plan = McpToolSchemas.PlanArgumentSchema
            .GetProperty("properties").GetProperty("plan");

        var required = plan.GetProperty("required")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToArray();
        required.Should().BeEmpty();

        plan.GetProperty("properties").GetProperty("steps")
            .TryGetProperty("minItems", out _).Should().BeFalse();
    }

    [UnitTest]
    public void ExecutePlanSchema_RequiresPlanIdAndNonEmptySteps_ToMatchSubmissionGuards()
    {
        // SubmitJobAsync rejects missing planId and empty steps at ingest; the
        // published schema must match so schema-driven clients do not send
        // payloads the server always refuses.
        var plan = McpToolSchemas.ExecutePlanArgumentSchema
            .GetProperty("properties").GetProperty("plan");

        var required = plan.GetProperty("required")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToArray();
        required.Should().BeEquivalentTo("planId", "steps");

        plan.GetProperty("properties").GetProperty("planId")
            .GetProperty("minLength").GetInt32().Should().Be(1);
        plan.GetProperty("properties").GetProperty("steps")
            .GetProperty("minItems").GetInt32().Should().Be(1);
    }

    [UnitTest]
    public void PackageReviewSchema_DoesNotRequireOrEnumLimitPackageFamily_SoServiceReportsRequestShapeFindings()
    {
        // The package-review service intentionally reviews missing and unknown
        // families and returns canonical missing_package_family /
        // unsupported_package_family findings. Marking packageFamily required or
        // enum-limiting it in the published schema would let strict JSON-schema
        // clients block the very inputs the tools are meant to inspect.
        var schema = McpToolSchemas.PackageReviewArgumentSchema;

        if (schema.TryGetProperty("required", out var required))
        {
            required.EnumerateArray()
                .Select(e => e.GetString())
                .Should().NotContain("packageFamily");
        }

        schema.GetProperty("properties").GetProperty("packageFamily")
            .TryGetProperty("enum", out _).Should().BeFalse();
    }

    [UnitTest]
    public void PackageReviewSchema_AllowsNullRequirementsAndResourceRefs_SoSchemaClientsMatchTheService()
    {
        // The request model normalizes an explicit null requirements/resourceRefs
        // to an empty set, so the published schema must permit null. A stricter
        // schema would let strict JSON-schema clients reject inputs the service
        // accepts and normalizes.
        var properties = McpToolSchemas.PackageReviewArgumentSchema.GetProperty("properties");

        SchemaTypeTokens(properties.GetProperty("requirements")).Should().Contain("null");
        SchemaTypeTokens(properties.GetProperty("resourceRefs")).Should().Contain("null");
    }

    private static string[] SchemaTypeTokens(JsonElement schema)
    {
        var type = schema.GetProperty("type");
        return type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Select(static e => e.GetString()!).ToArray()
            : [type.GetString()!];
    }

    private static string[] ExtractEnumValues(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var segment in path)
        {
            current.ValueKind.Should().Be(JsonValueKind.Object,
                $"path segment '{segment}' requires an object parent");
            current.TryGetProperty(segment, out var next).Should().BeTrue(
                $"schema must expose '{segment}' at the configured path");
            current = next;
        }

        current.ValueKind.Should().Be(JsonValueKind.Array, "the terminal node must be a JSON array");
        return current.EnumerateArray()
            .Select(e =>
            {
                e.ValueKind.Should().Be(JsonValueKind.String);
                return e.GetString()!;
            })
            .ToArray();
    }

    private static IMcpTool[] BuildTools()
    {
        var jobService = Substitute.For<IGeoprocessingJobService>();
        var groundingService = Substitute.For<IGroundingService>();
        return
        [
            new ValidatePlanTool(jobService, NullLogger<ValidatePlanTool>.Instance),
            new DryRunPlanTool(jobService, NullLogger<DryRunPlanTool>.Instance),
            new ExecutePlanTool(jobService, NullLogger<ExecutePlanTool>.Instance),
            new CancelJobTool(jobService, NullLogger<CancelJobTool>.Instance),
            new PlanAnalysisTool(
                Substitute.For<Honua.Server.Features.AiBuilder.Planning.IPlanAnalysisService>(),
                jobService,
                NullLogger<PlanAnalysisTool>.Instance),
            new GroundCandidatesTool(groundingService, jobService, NullLogger<GroundCandidatesTool>.Instance),
            new ClarifyIntentTool(groundingService, jobService, NullLogger<ClarifyIntentTool>.Instance)
        ];
    }

    private static IMcpResource[] BuildResources()
    {
        var jobService = Substitute.For<IGeoprocessingJobService>();
        var reportService = Substitute.For<Honua.Core.Features.Reporting.Abstractions.IAnalysisReportService>();
        var services = Substitute.For<IPublishedServiceStore>();
        var deployments = Substitute.For<IDeploymentStore>();
        return
        [
            new JobStatusResource(jobService, NullLogger<JobStatusResource>.Instance),
            new JobResultsResource(jobService, NullLogger<JobResultsResource>.Instance),
            new AnalysisReportResource(
                reportService,
                NullLogger<AnalysisReportResource>.Instance),
            new WorkspaceResource(jobService, NullLogger<WorkspaceResource>.Instance),
            new ProcessCatalogResource(jobService, NullLogger<ProcessCatalogResource>.Instance),
            new PublishedServiceResource(
                services, deployments, jobService,
                NullLogger<PublishedServiceResource>.Instance),
            new DeploymentResource(deployments, jobService, NullLogger<DeploymentResource>.Instance),
            new MapPackageResource(deployments, jobService, NullLogger<MapPackageResource>.Instance),
            new AppPackageResource(deployments, jobService, NullLogger<AppPackageResource>.Instance),
            new PromotionSurfaceIndexResource(
                services, deployments, jobService,
                NullLogger<PromotionSurfaceIndexResource>.Instance)
        ];
    }
}
