// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Mcp.Resources;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Mcp;

/// <summary>
/// Verifies that MCP resource handlers serialize domain records into the
/// canonical <c>honua://...</c> JSON envelope advertised in
/// <c>AI_OPERATOR_CONTRACT.md §9</c>. The tests pin the wire fields that
/// operators parse — job ids, resource URIs, map-package identifiers, and
/// stub markers — so accidental rename of a JSON property breaks the build.
/// </summary>
[Protocol(Protocols.Mcp)]
public sealed class McpResourceSerializationTests
{
    private readonly IGeoprocessingJobService _jobService = Substitute.For<IGeoprocessingJobService>();

    [UnitTest]
    [Endpoint("POST /mcp resources/read honua://jobs/{jobId}")]
    public async Task JobStatusResource_SerializesRecordFieldsAndLinksResultsUri()
    {
        var created = DateTimeOffset.Parse("2026-04-18T12:00:00Z", CultureInfo.InvariantCulture);
        var updated = created.AddMinutes(2);
        _jobService.GetJobAsync("job-xyz", Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(new ExecutionJobRecord
            {
                OperationId = "job-xyz",
                Status = ExecutionJobStatus.Running,
                CreatedAt = created,
                UpdatedAt = updated,
                CompletedAt = null,
                PercentComplete = 50.5,
                CurrentPhase = "executing",
                Warnings = ["layer projection assumed"],
                Spec = new ExecutionJobSpec
                {
                    Kind = ExecutionJobKind.Geoprocessing,
                    TargetKind = BatchComputeTargetKind.KubernetesJob,
                    Backend = "local",
                    WorkloadName = "buffer"
                }
            });

        var resource = new JobStatusResource(_jobService, NullLogger<JobStatusResource>.Instance);
        var result = await resource.ReadAsync(
            McpTestFactory.AuthenticatedHttpContext(), "honua://jobs/job-xyz", CancellationToken.None);

        result.Contents.Should().HaveCount(1);
        var content = result.Contents[0];
        content.Uri.Should().Be("honua://jobs/job-xyz");
        content.MimeType.Should().Be("application/json");

        var body = McpTestFactory.ParseJson(content.Text);
        body.GetProperty("jobId").GetString().Should().Be("job-xyz");
        body.GetProperty("status").GetString().Should().Be("Running");
        body.GetProperty("percentComplete").GetDouble().Should().Be(50.5);
        body.GetProperty("currentPhase").GetString().Should().Be("executing");
        body.GetProperty("warnings").GetArrayLength().Should().Be(1);
        body.GetProperty("resultsUri").GetString().Should().Be("honua://jobs/job-xyz/results");
    }

    [UnitTest]
    [Endpoint("POST /mcp resources/read honua://jobs/{jobId}/results")]
    public async Task JobResultsResource_ReturnsNotImplementedEnvelopeWithJobId()
    {
        var resource = new JobResultsResource(NullLogger<JobResultsResource>.Instance);

        var result = await resource.ReadAsync(
            McpTestFactory.AuthenticatedHttpContext(),
            "honua://jobs/job-xyz/results",
            CancellationToken.None);

        result.Contents.Should().HaveCount(1);
        var content = result.Contents[0];
        content.Uri.Should().Be("honua://jobs/job-xyz/results");
        content.MimeType.Should().Be("application/json");

        var body = McpTestFactory.ParseJson(content.Text);
        body.GetProperty("jobId").GetString().Should().Be("job-xyz");
        body.GetProperty("status").GetString().Should().Be("not_implemented");
        body.GetProperty("notImplementedReason").GetString().Should().NotBeNullOrEmpty();

        // Package fields are reserved on the wire until result storage ships.
        body.GetProperty("resultPackageId").GetString().Should().BeEmpty();
        body.GetProperty("artifacts").GetArrayLength().Should().Be(0);
        body.GetProperty("workspaceRefs").GetArrayLength().Should().Be(0);
        // mapPackageId is nullable; the context omits null properties.
        body.TryGetProperty("mapPackageId", out _).Should().BeFalse();
    }

    [UnitTest]
    [Endpoint("POST /mcp resources/read honua://workspaces/{workspaceId}")]
    public async Task WorkspaceResource_ReturnsNotImplementedEnvelopeWithWorkspaceId()
    {
        var resource = new WorkspaceResource(NullLogger<WorkspaceResource>.Instance);

        var result = await resource.ReadAsync(
            McpTestFactory.AuthenticatedHttpContext(),
            "honua://workspaces/ws-42",
            CancellationToken.None);

        result.Contents.Should().HaveCount(1);
        var body = McpTestFactory.ParseJson(result.Contents[0].Text);
        body.GetProperty("workspaceId").GetString().Should().Be("ws-42");
        body.GetProperty("status").GetString().Should().Be("not_implemented");
        body.GetProperty("notImplementedReason").GetString().Should().NotBeNullOrEmpty();
    }

    [UnitTest]
    [Endpoint("POST /mcp resources/read honua://catalog/processes")]
    public async Task ProcessCatalogResource_ReturnsEmptyCatalogWithNotImplementedStatus()
    {
        var resource = new ProcessCatalogResource(NullLogger<ProcessCatalogResource>.Instance);

        var result = await resource.ReadAsync(
            McpTestFactory.AuthenticatedHttpContext(),
            "honua://catalog/processes",
            CancellationToken.None);

        var body = McpTestFactory.ParseJson(result.Contents[0].Text);
        body.GetProperty("status").GetString().Should().Be("not_implemented");
        body.GetProperty("processes").GetArrayLength().Should().Be(0);
        body.GetProperty("notImplementedReason").GetString().Should().NotBeNullOrEmpty();
    }

    [UnitTest]
    [Endpoint("POST /mcp resources/read")]
    public void JobStatusResource_CanHandle_MatchesOnlyBareJobUris()
    {
        var resource = new JobStatusResource(_jobService, NullLogger<JobStatusResource>.Instance);

        resource.CanHandle("honua://jobs/job-1").Should().BeTrue();
        resource.CanHandle("honua://jobs/job-1/results").Should().BeFalse();
        resource.CanHandle("honua://workspaces/job-1").Should().BeFalse();
        resource.CanHandle("honua://jobs/").Should().BeFalse();
    }

    [UnitTest]
    [Endpoint("POST /mcp resources/read")]
    public void JobResultsResource_CanHandle_MatchesOnlyJobResultsUris()
    {
        var resource = new JobResultsResource(NullLogger<JobResultsResource>.Instance);

        resource.CanHandle("honua://jobs/job-1/results").Should().BeTrue();
        resource.CanHandle("honua://jobs/job-1").Should().BeFalse();
        resource.CanHandle("honua://jobs//results").Should().BeFalse();
    }

    [UnitTest]
    [Endpoint("POST /mcp resources/read")]
    public void WorkspaceResource_CanHandle_MatchesOnlyWorkspaceUris()
    {
        var resource = new WorkspaceResource(NullLogger<WorkspaceResource>.Instance);

        resource.CanHandle("honua://workspaces/ws-1").Should().BeTrue();
        resource.CanHandle("honua://workspaces/").Should().BeFalse();
        resource.CanHandle("honua://workspaces/ws-1/sub").Should().BeFalse();
        resource.CanHandle("honua://jobs/ws-1").Should().BeFalse();
    }

    [UnitTest]
    [Endpoint("POST /mcp resources/read")]
    public void ProcessCatalogResource_CanHandle_MatchesOnlyCatalogUri()
    {
        var resource = new ProcessCatalogResource(NullLogger<ProcessCatalogResource>.Instance);

        resource.CanHandle("honua://catalog/processes").Should().BeTrue();
        resource.CanHandle("honua://catalog/processes/foo").Should().BeFalse();
        resource.CanHandle("honua://catalog").Should().BeFalse();
    }
}
