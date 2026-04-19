// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Mcp;
using Honua.Server.Features.Mcp.Resources;
using Honua.Server.Features.Mcp.Tools;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Mcp;

/// <summary>
/// Pins the MCP tool names, resource URIs, and workflow-family tags to the
/// geospatial-mcp taxonomy v1 as described in <c>AI_OPERATOR_CONTRACT.md §MCP
/// Contract Families</c>. A rename on either side trips this test.
/// </summary>
[Protocol(Protocols.Mcp)]
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
        "honua://workspaces/{workspaceId}",
        "honua://catalog/processes"
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
            McpTelemetry.ResourceFamily.Workspaces,
            McpTelemetry.ResourceFamily.Catalog
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

    private static IMcpTool[] BuildTools()
    {
        var jobService = Substitute.For<IGeoprocessingJobService>();
        return
        [
            new ValidatePlanTool(jobService, NullLogger<ValidatePlanTool>.Instance),
            new DryRunPlanTool(jobService, NullLogger<DryRunPlanTool>.Instance),
            new ExecutePlanTool(jobService, NullLogger<ExecutePlanTool>.Instance),
            new CancelJobTool(jobService, NullLogger<CancelJobTool>.Instance),
            new PlanAnalysisTool(NullLogger<PlanAnalysisTool>.Instance),
            new GroundCandidatesTool(NullLogger<GroundCandidatesTool>.Instance),
            new ClarifyIntentTool(NullLogger<ClarifyIntentTool>.Instance)
        ];
    }

    private static IMcpResource[] BuildResources()
    {
        var jobService = Substitute.For<IGeoprocessingJobService>();
        return
        [
            new JobStatusResource(jobService, NullLogger<JobStatusResource>.Instance),
            new JobResultsResource(jobService, NullLogger<JobResultsResource>.Instance),
            new WorkspaceResource(NullLogger<WorkspaceResource>.Instance),
            new ProcessCatalogResource(NullLogger<ProcessCatalogResource>.Instance)
        ];
    }
}
