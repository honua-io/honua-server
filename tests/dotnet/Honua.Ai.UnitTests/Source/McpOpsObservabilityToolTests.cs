// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Resources;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Unit coverage for the read-only MCP operational-observability tools and
/// fixed ops resources (#2555).
/// </summary>
public sealed class McpOpsObservabilityToolTests
{
    [UnitTest]
    public async Task OpsHealthTool_Invoke_ReturnsReaderPayload()
    {
        var reader = new FakeOpsObservabilityReader();
        var context = BuildContext(reader);
        var tool = new OpsHealthTool(NullLogger<OpsHealthTool>.Instance);

        var result = await tool.InvokeAsync(context, Json("{}"), CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.StructuredContent!.Value.GetProperty("overallStatus").GetString().Should().Be("Healthy");
        reader.HealthCalls.Should().Be(1);
        reader.LastPrincipal.Should().BeSameAs(context.User);
    }

    [UnitTest]
    public async Task OpsFindingsTool_Invoke_PassesFiltersToReader()
    {
        var reader = new FakeOpsObservabilityReader();
        var context = BuildContext(reader);
        var tool = new OpsFindingsTool(NullLogger<OpsFindingsTool>.Instance);
        var arguments = Json(
            """
            {
              "findingId": "deploy-skew-1",
              "severity": "Warning",
              "rule": "platform-release-skew"
            }
            """);

        var result = await tool.InvokeAsync(context, arguments, CancellationToken.None);

        result.IsError.Should().BeFalse();
        reader.FindingsArgument.Should().NotBeNull();
        reader.FindingsArgument!.FindingId.Should().Be("deploy-skew-1");
        reader.FindingsArgument.Severity.Should().Be("Warning");
        reader.FindingsArgument.Rule.Should().Be("platform-release-skew");
    }

    [UnitTest]
    public async Task AlertEventsTool_Invoke_PassesFiltersToReader()
    {
        var reader = new FakeOpsObservabilityReader();
        var context = BuildContext(reader);
        var tool = new AlertEventsTool(NullLogger<AlertEventsTool>.Instance);
        var arguments = Json(
            """
            {
              "source": "gis",
              "severity": "critical",
              "rule": "17",
              "lifecycleState": "open",
              "from": "2026-07-01T00:00:00Z",
              "to": "2026-07-02T00:00:00Z",
              "pageSize": 25,
              "cursor": "next-1"
            }
            """);

        var result = await tool.InvokeAsync(context, arguments, CancellationToken.None);

        result.IsError.Should().BeFalse();
        reader.AlertArgument.Should().NotBeNull();
        reader.AlertArgument!.Source.Should().Be("gis");
        reader.AlertArgument.Severity.Should().Be("critical");
        reader.AlertArgument.Rule.Should().Be("17");
        reader.AlertArgument.LifecycleState.Should().Be("open");
        reader.AlertArgument.From.Should().Be(DateTimeOffset.Parse("2026-07-01T00:00:00Z", CultureInfo.InvariantCulture));
        reader.AlertArgument.To.Should().Be(DateTimeOffset.Parse("2026-07-02T00:00:00Z", CultureInfo.InvariantCulture));
        reader.AlertArgument.PageSize.Should().Be(25);
        reader.AlertArgument.Cursor.Should().Be("next-1");
    }

    [UnitTest]
    public async Task OperateEventsTool_Invoke_PassesFiltersToReader()
    {
        var reader = new FakeOpsObservabilityReader();
        var context = BuildContext(reader);
        var tool = new OperateEventsTool(NullLogger<OperateEventsTool>.Instance);
        var arguments = Json(
            """
            {
              "kind": ["release", "job"],
              "correlationId": "corr-1",
              "operationId": "op-1",
              "releaseId": "2026.07.01",
              "from": "2026-07-01T00:00:00Z",
              "to": "2026-07-02T00:00:00Z",
              "pageSize": 40
            }
            """);

        var result = await tool.InvokeAsync(context, arguments, CancellationToken.None);

        result.IsError.Should().BeFalse();
        reader.OperateArgument.Should().NotBeNull();
        reader.OperateArgument!.Kind.Should().BeEquivalentTo(["release", "job"]);
        reader.OperateArgument.CorrelationId.Should().Be("corr-1");
        reader.OperateArgument.OperationId.Should().Be("op-1");
        reader.OperateArgument.ReleaseId.Should().Be("2026.07.01");
        reader.OperateArgument.PageSize.Should().Be(40);
    }

    [UnitTest]
    public async Task OpsResources_Read_ReturnReaderPayloads()
    {
        var reader = new FakeOpsObservabilityReader();
        var context = BuildContext(reader);
        var health = new OpsHealthResource(NullLogger<OpsHealthResource>.Instance);
        var findings = new OpsFindingsResource(NullLogger<OpsFindingsResource>.Instance);

        var healthResult = await health.ReadAsync(context, McpResourceUris.OpsHealth, CancellationToken.None);
        var findingsResult = await findings.ReadAsync(context, McpResourceUris.OpsFindings, CancellationToken.None);

        health.CanHandle(McpResourceUris.OpsHealth).Should().BeTrue();
        findings.CanHandle(McpResourceUris.OpsFindings).Should().BeTrue();
        healthResult.Contents.Single().Text.Should().Contain("\"overallStatus\":\"Healthy\"");
        findingsResult.Contents.Single().Text.Should().Contain("\"findings\"");
        reader.HealthCalls.Should().Be(1);
        reader.FindingsArgument.Should().NotBeNull();
        reader.FindingsArgument!.FindingId.Should().BeNull();
    }

    private static DefaultHttpContext BuildContext(IMcpOpsObservabilityReader reader)
    {
        var services = new ServiceCollection();
        services.AddSingleton(reader);
        var provider = services.BuildServiceProvider();

        return new DefaultHttpContext
        {
            RequestServices = provider,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "mcp-test")],
                "Test"))
        };
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class FakeOpsObservabilityReader : IMcpOpsObservabilityReader
    {
        public int HealthCalls { get; private set; }

        public ClaimsPrincipal? LastPrincipal { get; private set; }

        public McpOpsFindingsArgument? FindingsArgument { get; private set; }

        public McpAlertEventsArgument? AlertArgument { get; private set; }

        public McpOperateEventsArgument? OperateArgument { get; private set; }

        public Task<JsonElement> GetOpsHealthAsync(
            ClaimsPrincipal principal,
            CancellationToken cancellationToken)
        {
            LastPrincipal = principal;
            HealthCalls++;
            return Task.FromResult(Json("""{"generatedAt":"2026-07-01T00:00:00Z","overallStatus":"Healthy"}"""));
        }

        public Task<JsonElement> GetOpsFindingsAsync(
            ClaimsPrincipal principal,
            McpOpsFindingsArgument argument,
            CancellationToken cancellationToken)
        {
            LastPrincipal = principal;
            FindingsArgument = argument;
            return Task.FromResult(Json("""{"generatedAt":"2026-07-01T00:00:00Z","findings":[]}"""));
        }

        public Task<JsonElement> ListAlertEventsAsync(
            ClaimsPrincipal principal,
            McpAlertEventsArgument argument,
            CancellationToken cancellationToken)
        {
            LastPrincipal = principal;
            AlertArgument = argument;
            return Task.FromResult(Json("""{"items":[],"nextCursor":"next-2"}"""));
        }

        public Task<JsonElement> ListOperateEventsAsync(
            ClaimsPrincipal principal,
            McpOperateEventsArgument argument,
            CancellationToken cancellationToken)
        {
            LastPrincipal = principal;
            OperateArgument = argument;
            return Task.FromResult(Json("""{"items":[],"partialResult":false}"""));
        }
    }
}
