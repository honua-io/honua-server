// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Unit coverage for MCP platform-ops tools (#2566).
/// </summary>
public sealed class McpPlatformOpsToolTests
{
    [UnitTest]
    public async Task PlatformReleaseStatusTool_Invoke_ReturnsReaderPayload()
    {
        var reader = new FakePlatformOpsReader();
        var context = BuildContext(reader);
        var tool = new PlatformReleaseStatusTool(NullLogger<PlatformReleaseStatusTool>.Instance);

        var result = await tool.InvokeAsync(context, Json("{}"), CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.StructuredContent!.Value.GetProperty("releaseDeclared").GetBoolean().Should().BeTrue();
        reader.PlatformReleaseStatusCalls.Should().Be(1);
        reader.LastPrincipal.Should().BeSameAs(context.User);
    }

    [UnitTest]
    public async Task DeployOperationsTool_Invoke_PassesFiltersToReader()
    {
        var reader = new FakePlatformOpsReader();
        var context = BuildContext(reader);
        var tool = new DeployOperationsTool(NullLogger<DeployOperationsTool>.Instance);
        var arguments = Json(
            """
            {
              "operationId": "op-1",
              "status": "Submitted",
              "kind": "Deploy",
              "page": 2,
              "pageSize": 25
            }
            """);

        var result = await tool.InvokeAsync(context, arguments, CancellationToken.None);

        result.IsError.Should().BeFalse();
        reader.DeployOperationsArgument.Should().NotBeNull();
        reader.DeployOperationsArgument!.OperationId.Should().Be("op-1");
        reader.DeployOperationsArgument.Status.Should().Be("Submitted");
        reader.DeployOperationsArgument.Kind.Should().Be("Deploy");
        reader.DeployOperationsArgument.Page.Should().Be(2);
        reader.DeployOperationsArgument.PageSize.Should().Be(25);
    }

    [UnitTest]
    public async Task ProposeRollbackTool_Invoke_PassesArgumentsAndReturnsProposal()
    {
        var reader = new FakePlatformOpsReader();
        var context = BuildContext(reader);
        var tool = new ProposeRollbackTool(NullLogger<ProposeRollbackTool>.Instance);
        var arguments = Json(
            """
            {
              "targetId": "serving-us-west",
              "toRevision": "rev-9",
              "reason": "SLO regression",
              "idempotencyKey": "rb-1"
            }
            """);

        var result = await tool.InvokeAsync(context, arguments, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.StructuredContent!.Value.GetProperty("proposalId").GetString().Should().Be("proposal-1");
        reader.RollbackArgument.Should().NotBeNull();
        reader.RollbackArgument!.TargetId.Should().Be("serving-us-west");
        reader.RollbackArgument.ToRevision.Should().Be("rev-9");
        reader.RollbackArgument.Reason.Should().Be("SLO regression");
        reader.RollbackArgument.IdempotencyKey.Should().Be("rb-1");
    }

    private static DefaultHttpContext BuildContext(IMcpPlatformOpsReader reader)
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

    private sealed class FakePlatformOpsReader : IMcpPlatformOpsReader
    {
        public int PlatformReleaseStatusCalls { get; private set; }

        public ClaimsPrincipal? LastPrincipal { get; private set; }

        public McpDeployOperationsArgument? DeployOperationsArgument { get; private set; }

        public McpProposeRollbackArgument? RollbackArgument { get; private set; }

        public Task<JsonElement> GetPlatformReleaseStatusAsync(
            ClaimsPrincipal principal,
            CancellationToken cancellationToken)
        {
            LastPrincipal = principal;
            PlatformReleaseStatusCalls++;
            return Task.FromResult(Json("""{"releaseDeclared":true,"isCoVersioned":true,"serving":[],"execution":[],"skewedIds":[]}"""));
        }

        public Task<JsonElement> GetDeployOperationsAsync(
            ClaimsPrincipal principal,
            McpDeployOperationsArgument argument,
            CancellationToken cancellationToken)
        {
            LastPrincipal = principal;
            DeployOperationsArgument = argument;
            return Task.FromResult(Json("""{"items":[],"page":1,"pageSize":50,"totalCount":0,"hasMore":false}"""));
        }

        public Task<McpProposeOperationOutput> ProposeRollbackAsync(
            ClaimsPrincipal principal,
            McpProposeRollbackArgument argument,
            CancellationToken cancellationToken)
        {
            LastPrincipal = principal;
            RollbackArgument = argument;
            return Task.FromResult(new McpProposeOperationOutput
            {
                Outcome = "ProposalCreated",
                RequiresApproval = true,
                ProposalId = "proposal-1",
                ResourceUri = "honua://proposals/proposal-1",
            });
        }
    }
}
