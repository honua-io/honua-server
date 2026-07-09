// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Deployment.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Grounding.Abstractions;
using Honua.Core.Features.Publishing.Abstractions;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Resources;
using Honua.Ai.Protocols.Mcp.Tools;
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
public sealed partial class McpTaxonomyAlignmentTests
{
    private static readonly string[] TaxonomyToolNames =
    {
        "honua_validate_plan",
        "honua_execute_plan",
        "honua_dry_run_plan",
        "honua_cancel_job",
        "honua_propose_operation",
        "honua_publish_service",
        "honua_publish_result",
        "honua_create_map_package",
        "honua_create_app_package",
        "honua_plan_analysis",
        "honua_ground_candidates",
        "honua_clarify_intent",
        "honua_geocode_address",
        "honua_geocode_addresses",
        "honua_ingest_dataset",
        "honua_solve_route",
        "honua_ops_health",
        "honua_ops_findings",
        "honua_alert_events",
        "honua_operate_events",
        "honua_platform_release_status",
        "honua_deploy_operations",
        "honua_propose_rollback",
        "honua_list_layers",
        "honua_query_features",
        // No honua_edit_features: Honua exposes no AI/MCP feature-mutation tool
        // per ADR-0028 (AI operational data editing is not supported).
        "honua_render_map",
        "honua_get_style",
        "honua_apply_style_preset",
        "honua_resolve_entity",
        "honua_list_capabilities"
    };

    private static readonly string[] TaxonomyResourceUris =
    {
        "honua://jobs/{jobId}",
        "honua://jobs/{jobId}/results",
        "honua://jobs/{jobId}/report",
        "honua://proposals/{proposalId}",
        "honua://workspaces/{workspaceId}",
        "honua://catalog/processes",
        "honua://catalog/features",
        "honua://ops/health",
        "honua://ops/findings",
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
        new GeocodeTool(jobService, NullLogger<GeocodeTool>.Instance)
            .WorkflowFamily.Should().Be(McpTelemetry.WorkflowFamily.Execution);
        new RouteTool(jobService, NullLogger<RouteTool>.Instance)
            .WorkflowFamily.Should().Be(McpTelemetry.WorkflowFamily.Execution);
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
            McpTelemetry.ResourceFamily.Proposals,
            McpTelemetry.ResourceFamily.Workspaces,
            McpTelemetry.ResourceFamily.Catalog,
            McpTelemetry.ResourceFamily.OpsHealth,
            McpTelemetry.ResourceFamily.OpsFindings,
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

    // -------------------------------------------------------------------
    // Tool annotations (#1953): every tool must advertise read/write hints
    // -------------------------------------------------------------------

    /// <summary>
    /// Read-only tools per the geospatial-mcp taxonomy: planning, grounding,
    /// validation, and map read tools never mutate server state. (The
    /// package-review tools are read-only too but are constructed separately in
    /// <c>PackageReviewTools_AreReadOnly_WithOutputSchemas</c> because they take
    /// extra service dependencies that <see cref="BuildTools"/> does not wire.)
    /// </summary>
    private static readonly HashSet<string> ReadOnlyToolNames = new(StringComparer.Ordinal)
    {
        "honua_validate_plan",
        "honua_dry_run_plan",
        "honua_plan_analysis",
        "honua_ground_candidates",
        "honua_clarify_intent",
        "honua_geocode_address",
        "honua_geocode_addresses",
        "honua_solve_route",
        "honua_ops_health",
        "honua_ops_findings",
        "honua_alert_events",
        "honua_operate_events",
        "honua_platform_release_status",
        "honua_deploy_operations",
        "honua_list_layers",
        "honua_query_features",
        "honua_render_map",
        "honua_get_style",
        "honua_resolve_entity",
        "honua_list_capabilities",
    };

    /// <summary>
    /// Write tools and their expected destructive/idempotent classification.
    /// </summary>
    private static readonly Dictionary<string, (bool Destructive, bool Idempotent)> WriteToolExpectations =
        new(StringComparer.Ordinal)
        {
            ["honua_execute_plan"] = (Destructive: false, Idempotent: true),
            ["honua_cancel_job"] = (Destructive: true, Idempotent: true),
            ["honua_propose_operation"] = (Destructive: false, Idempotent: true),
            ["honua_propose_rollback"] = (Destructive: false, Idempotent: true),
            ["honua_ingest_dataset"] = (Destructive: false, Idempotent: false),
            ["honua_publish_service"] = (Destructive: false, Idempotent: false),
            ["honua_publish_result"] = (Destructive: false, Idempotent: false),
            ["honua_create_map_package"] = (Destructive: false, Idempotent: false),
            ["honua_create_app_package"] = (Destructive: false, Idempotent: false),
            ["honua_apply_style_preset"] = (Destructive: false, Idempotent: true),
        };

    [UnitTest]
    public void EveryTool_AdvertisesAnnotations_WithTitle()
    {
        // Guard test: a NEW tool added without annotations (or without a title)
        // fails here, forcing the author to classify it read-only vs write.
        var tools = BuildTools();

        foreach (var tool in tools)
        {
            var descriptor = tool.Describe();
            descriptor.Annotations.Should().NotBeNull(
                $"tool '{tool.Name}' must advertise MCP tool annotations (#1953)");
            descriptor.Annotations!.ReadOnlyHint.Should().NotBeNull(
                $"tool '{tool.Name}' must classify itself read-only vs write");
            descriptor.Annotations.Title.Should().NotBeNullOrWhiteSpace(
                $"tool '{tool.Name}' must carry a human-readable annotation title");
            descriptor.Title.Should().NotBeNullOrWhiteSpace(
                $"tool '{tool.Name}' must carry a top-level display title");
        }
    }

    [UnitTest]
    public void EveryTool_IsClassifiedInTheExpectedRosters()
    {
        // The read-only roster and write roster together must cover every tool;
        // a tool missing from both rosters fails here so the test stays a
        // complete guard against an unclassified new tool.
        var toolNames = BuildTools().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var classified = new HashSet<string>(ReadOnlyToolNames, StringComparer.Ordinal);
        classified.UnionWith(WriteToolExpectations.Keys);

        toolNames.Should().BeEquivalentTo(classified,
            "every tool must appear in exactly one of the read-only or write rosters");
    }

    [UnitTest]
    public void ReadOnlyTools_FlagReadOnlyAndNonDestructive()
    {
        var tools = BuildTools().Where(t => ReadOnlyToolNames.Contains(t.Name));

        foreach (var tool in tools)
        {
            var annotations = tool.Describe().Annotations!;
            annotations.ReadOnlyHint.Should().BeTrue($"'{tool.Name}' is a read-only tool");
            annotations.DestructiveHint.Should().BeFalse($"'{tool.Name}' must not be flagged destructive");
        }
    }

    [UnitTest]
    public void WriteTools_AreFlaggedWrite_WithCorrectDestructiveAndIdempotentHints()
    {
        var tools = BuildTools().Where(t => WriteToolExpectations.ContainsKey(t.Name));
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var tool in tools)
        {
            var annotations = tool.Describe().Annotations!;
            var (destructive, idempotent) = WriteToolExpectations[tool.Name];

            annotations.ReadOnlyHint.Should().BeFalse($"'{tool.Name}' is a write tool");
            annotations.DestructiveHint.Should().Be(destructive, $"'{tool.Name}' destructiveHint");
            annotations.IdempotentHint.Should().Be(idempotent, $"'{tool.Name}' idempotentHint");
            seen.Add(tool.Name);
        }

        seen.Should().BeEquivalentTo(WriteToolExpectations.Keys,
            "every classified write tool must be present in the roster");
    }

    [UnitTest]
    public void ExecutePlan_IsFlaggedWriteIdempotentNonDestructive()
    {
        var jobService = Substitute.For<IGeoprocessingJobService>();
        var annotations = new ExecutePlanTool(jobService, NullLogger<ExecutePlanTool>.Instance)
            .Describe().Annotations!;

        annotations.ReadOnlyHint.Should().BeFalse();
        annotations.IdempotentHint.Should().BeTrue("execute honors the idempotencyKey");
        annotations.DestructiveHint.Should().BeFalse("execute creates a new job rather than destroying state");
    }

    [UnitTest]
    public void CancelJob_IsFlaggedWriteIdempotentDestructive()
    {
        var jobService = Substitute.For<IGeoprocessingJobService>();
        var annotations = new CancelJobTool(jobService, NullLogger<CancelJobTool>.Instance)
            .Describe().Annotations!;

        annotations.ReadOnlyHint.Should().BeFalse();
        annotations.IdempotentHint.Should().BeTrue("cancelling an already-cancelled job is a no-op");
        annotations.DestructiveHint.Should().BeTrue("cancel tears down in-flight execution");
    }

    [UnitTest]
    public void PackageReviewTools_AreReadOnly_WithOutputSchemas()
    {
        // validate_package / preview_package only inspect a package (preview also
        // computes a read-only preview plan); neither mutates server state.
        var reviewService = Substitute.For<Honua.Core.Features.PackageReview.Abstractions.IPackageReviewService>();
        var jobService = Substitute.For<IGeoprocessingJobService>();

        IMcpTool[] packageTools =
        [
            new ValidatePackageTool(reviewService, jobService, NullLogger<ValidatePackageTool>.Instance),
            new PreviewPackageTool(reviewService, jobService, NullLogger<PreviewPackageTool>.Instance),
        ];

        foreach (var tool in packageTools)
        {
            var descriptor = tool.Describe();
            descriptor.Title.Should().NotBeNullOrWhiteSpace();
            descriptor.Annotations.Should().NotBeNull();
            descriptor.Annotations!.ReadOnlyHint.Should().BeTrue($"'{tool.Name}' is read-only");
            descriptor.Annotations.DestructiveHint.Should().BeFalse();
            descriptor.OutputSchema.Should().NotBeNull($"'{tool.Name}' must publish an output schema");
            descriptor.OutputSchema!.Value.GetProperty("type").GetString().Should().Be("object");
        }
    }

    // -------------------------------------------------------------------
    // Output schemas (#1953): structuredContent schema per tool result
    // -------------------------------------------------------------------

    [UnitTest]
    public void StructuredTools_AdvertiseWellFormedOutputSchemas()
    {
        foreach (var tool in BuildTools())
        {
            var descriptor = tool.Describe();
            descriptor.OutputSchema.Should().NotBeNull(
                $"tool '{tool.Name}' must publish a structuredContent output schema (#1953)");

            var schema = descriptor.OutputSchema!.Value;
            schema.ValueKind.Should().Be(JsonValueKind.Object,
                $"'{tool.Name}' output schema must be a JSON-schema object");
            schema.GetProperty("type").GetString().Should().Be("object",
                $"'{tool.Name}' output schema root must be of type object");
            schema.TryGetProperty("properties", out var properties).Should().BeTrue(
                $"'{tool.Name}' output schema must declare properties");
            properties.ValueKind.Should().Be(JsonValueKind.Object);
            properties.EnumerateObject().Should().NotBeEmpty(
                $"'{tool.Name}' output schema must describe at least one property");
        }
    }

    [UnitTest]
    public void RenderMap_AdvertisesOutputSchemaForStructuredImageMetadata()
    {
        var jobService = Substitute.For<IGeoprocessingJobService>();
        var descriptor = new Honua.Ai.Protocols.Mcp.MapTools.RenderMapTool(
                jobService, NullLogger<Honua.Ai.Protocols.Mcp.MapTools.RenderMapTool>.Instance)
            .Describe();

        descriptor.OutputSchema.Should().NotBeNull();
        descriptor.OutputSchema!.Value.GetProperty("properties").TryGetProperty("delivery", out _).Should().BeTrue();
        descriptor.Annotations!.ReadOnlyHint.Should().BeTrue();
    }

    [UnitTest]
    public void DryRunOutputSchema_ArtifactEnum_MatchesArtifactKind()
    {
        // Like the input schemas, the dry-run output schema sources its artifact
        // enum from the canonical ArtifactKind so the contract cannot drift.
        var enumValues = ExtractEnumValues(McpToolOutputSchemas.DryRunOutputSchema,
            "properties", "estimatedArtifacts", "items", "enum");

        enumValues.Should().BeEquivalentTo(Enum.GetNames<ArtifactKind>());
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
            new ProposeOperationTool(NullLogger<ProposeOperationTool>.Instance),
            new PublishServiceTool(NullLogger<PublishServiceTool>.Instance),
            new PublishResultTool(jobService, NullLogger<PublishResultTool>.Instance),
            new CreateMapPackageTool(jobService, NullLogger<CreateMapPackageTool>.Instance),
            new CreateAppPackageTool(jobService, NullLogger<CreateAppPackageTool>.Instance),
            new PlanAnalysisTool(
                Substitute.For<Honua.Ai.AiBuilder.Planning.IPlanAnalysisService>(),
                jobService,
                NullLogger<PlanAnalysisTool>.Instance),
            new GroundCandidatesTool(groundingService, jobService, NullLogger<GroundCandidatesTool>.Instance),
            new ClarifyIntentTool(groundingService, jobService, NullLogger<ClarifyIntentTool>.Instance),
            new GeocodeTool(jobService, NullLogger<GeocodeTool>.Instance),
            new GeocodeAddressesTool(jobService, NullLogger<GeocodeAddressesTool>.Instance),
            new IngestDatasetTool(jobService, NullLogger<IngestDatasetTool>.Instance),
            new RouteTool(jobService, NullLogger<RouteTool>.Instance),
            new OpsHealthTool(NullLogger<OpsHealthTool>.Instance),
            new OpsFindingsTool(NullLogger<OpsFindingsTool>.Instance),
            new AlertEventsTool(NullLogger<AlertEventsTool>.Instance),
            new OperateEventsTool(NullLogger<OperateEventsTool>.Instance),
            new PlatformReleaseStatusTool(NullLogger<PlatformReleaseStatusTool>.Instance),
            new DeployOperationsTool(NullLogger<DeployOperationsTool>.Instance),
            new ProposeRollbackTool(NullLogger<ProposeRollbackTool>.Instance),
            new Honua.Ai.Protocols.Mcp.MapTools.ListLayersTool(
                jobService, NullLogger<Honua.Ai.Protocols.Mcp.MapTools.ListLayersTool>.Instance),
            new Honua.Ai.Protocols.Mcp.MapTools.QueryFeaturesTool(
                jobService, NullLogger<Honua.Ai.Protocols.Mcp.MapTools.QueryFeaturesTool>.Instance),
            new Honua.Ai.Protocols.Mcp.MapTools.RenderMapTool(
                jobService, NullLogger<Honua.Ai.Protocols.Mcp.MapTools.RenderMapTool>.Instance),
            new Honua.Ai.Protocols.Mcp.MapTools.GetStyleTool(
                jobService, NullLogger<Honua.Ai.Protocols.Mcp.MapTools.GetStyleTool>.Instance),
            new Honua.Ai.Protocols.Mcp.MapTools.ApplyStylePresetTool(
                jobService, NullLogger<Honua.Ai.Protocols.Mcp.MapTools.ApplyStylePresetTool>.Instance),
            new Honua.Ai.Protocols.Mcp.Discovery.ResolveEntityTool(
                jobService, NullLogger<Honua.Ai.Protocols.Mcp.Discovery.ResolveEntityTool>.Instance),
            new Honua.Ai.Protocols.Mcp.Discovery.ListCapabilitiesTool(
                jobService, NullLogger<Honua.Ai.Protocols.Mcp.Discovery.ListCapabilitiesTool>.Instance)
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
            new ProposalStatusResource(NullLogger<ProposalStatusResource>.Instance),
            new AnalysisReportResource(
                reportService,
                NullLogger<AnalysisReportResource>.Instance),
            new WorkspaceResource(jobService, NullLogger<WorkspaceResource>.Instance),
            new ProcessCatalogResource(jobService, NullLogger<ProcessCatalogResource>.Instance),
            new FeatureCatalogResource(jobService, NullLogger<FeatureCatalogResource>.Instance),
            new OpsHealthResource(NullLogger<OpsHealthResource>.Instance),
            new OpsFindingsResource(NullLogger<OpsFindingsResource>.Instance),
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
