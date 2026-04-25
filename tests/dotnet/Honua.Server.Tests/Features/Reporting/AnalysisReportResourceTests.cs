// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Reporting.Domain;
using Honua.Server.Features.Reporting;
using Honua.Server.Features.Reporting.Mcp;
using Honua.Server.Features.Reporting.Models;
using Honua.Server.Tests.Features.Protocols.Mcp;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Reporting;

/// <summary>
/// Verifies that the MCP report resource renders the canonical
/// <see cref="AnalysisReport"/> into a JSON envelope that round-trips through
/// the source-generated context without reflection.
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class AnalysisReportResourceTests
{
    [UnitTest]
    [Operation(Operations.ContractTesting)]
    [Endpoint("POST /mcp resources/read honua://jobs/{jobId}/report")]
    public async Task ReadAsync_DelegatesToReportServiceAndSerializesReport()
    {
        var reportService = Substitute.For<IAnalysisReportService>();
        var report = BuildFakeReport();
        reportService.GetReportAsync("job-xyz", Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(report);

        var resource = new AnalysisReportResource(reportService, NullLogger<AnalysisReportResource>.Instance);
        var result = await resource.ReadAsync(
            McpTestFactory.AuthenticatedHttpContext(),
            "honua://jobs/job-xyz/report",
            CancellationToken.None);

        result.Contents.Should().HaveCount(1);
        var content = result.Contents[0];
        content.Uri.Should().Be("honua://jobs/job-xyz/report");
        content.MimeType.Should().Be("application/json");

        var body = JsonDocument.Parse(content.Text).RootElement;
        body.GetProperty("jobId").GetString().Should().Be("job-xyz");
        body.GetProperty("resultPackageId").GetString().Should().Be("pkg-1");
        body.GetProperty("reportContractVersion").GetString().Should().Be(ReportingConstants.ContractVersionV1);
        body.GetProperty("templateId").GetString().Should().Be("analysis-report.analytics-buffer-aggregate");
        body.GetProperty("templateVersion").GetString().Should().Be("1.0.0");
        body.GetProperty("narrativeMode").GetString().Should().Be(ReportingConstants.NarrativeModeDeterministicTag);
        body.GetProperty("sections").GetArrayLength().Should().BeGreaterThan(0);
        body.GetProperty("renderUris").GetProperty("markdown").GetString().Should().Contain("/render?format=md");
        body.GetProperty("renderUris").GetProperty("html").GetString().Should().Contain("/render?format=html");
    }

    [UnitTest]
    [Operation(Operations.ContractTesting)]
    public void CanHandle_AcceptsReportUriAndRejectsResultsUri()
    {
        var resource = new AnalysisReportResource(
            Substitute.For<IAnalysisReportService>(),
            NullLogger<AnalysisReportResource>.Instance);

        resource.CanHandle("honua://jobs/job-1/report").Should().BeTrue();
        resource.CanHandle("honua://jobs/job-1/results").Should().BeFalse();
        resource.CanHandle("honua://jobs/").Should().BeFalse();
        resource.CanHandle("honua://jobs//report").Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.ContractTesting)]
    public void ReportingJsonContext_RoundTripsAnalysisReport()
    {
        var original = BuildFakeReport();
        var json = JsonSerializer.Serialize(original, ReportingJsonContext.Default.AnalysisReport);
        var restored = JsonSerializer.Deserialize(json, ReportingJsonContext.Default.AnalysisReport);

        restored.Should().NotBeNull();
        restored!.ReportId.Should().Be(original.ReportId);
        restored.Sections.Should().HaveSameCount(original.Sections);
        restored.Sections.OfType<NarrativeSection>().Single().DeterministicText
            .Should().Be(original.Sections.OfType<NarrativeSection>().Single().DeterministicText);
    }

    private static AnalysisReport BuildFakeReport() => ReportingFixtures.CreateBuilder()
        .BuildAsync("job-xyz", ReportingFixtures.BufferAggregatePackage(), CancellationToken.None)
        .GetAwaiter()
        .GetResult() with { JobId = "job-xyz", ResultPackageId = "pkg-1" };
}
