// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.WorkflowPackages.Abstractions;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed class WorkflowPackageToolTests
{
    [UnitTest]
    public async Task Read_ForwardsActualPrincipalAndExplicitTurn_AndReturnsImmutableVersion()
    {
        var authoring = Substitute.For<IWorkflowPackageAuthoring>();
        var context = Context(authoring);
        var version = new WorkflowPackageVersion
        {
            PackageId = "package-a",
            Version = 3,
            PackageHash = "hash-a",
            Graph = new() { Nodes = [] },
            Validation = WorkflowPackageValidationResult.Success("hash-a"),
            CreatedAt = DateTimeOffset.UnixEpoch,
        };
        authoring.ReadAsync(context.User, "workspace-a", "turn-a", "package-a", 3, Arg.Any<CancellationToken>()).Returns(version);
        var result = await new WorkflowPackageTool().InvokeAsync(context, JsonSerializer.SerializeToElement(new
        {
            action = "read",
            workspaceId = "workspace-a",
            turnId = "turn-a",
            packageId = "package-a",
            version = 3,
        }), CancellationToken.None);
        result.IsError.Should().BeFalse();
        result.StructuredContent!.Value.GetProperty("version").GetProperty("packageHash").GetString().Should().Be("hash-a");
        result.StructuredContent.Value.GetProperty("qualification").GetString().Should().Be("validated-not-execution-verified");
        await authoring.Received(1).ReadAsync(context.User, "workspace-a", "turn-a", "package-a", 3, Arg.Any<CancellationToken>());
    }

    [UnitTheory]
    [InlineData("""{"action":"create","workspaceId":"w","turnId":"t","packageId":"existing","name":"Edit","graph":{"nodes":[]}}""")]
    [InlineData("""{"action":"publish","workspaceId":"w","turnId":"t"}""")]
    [InlineData("""{"action":"list","workspaceId":"w"}""")]
    public async Task InvalidOrUpsertOrGovernanceArguments_DoNotReachAuthoring(string json)
    {
        var authoring = Substitute.For<IWorkflowPackageAuthoring>();
        var invoke = () => new WorkflowPackageTool().InvokeAsync(Context(authoring), JsonDocument.Parse(json).RootElement, CancellationToken.None);
        await invoke.Should().ThrowAsync<GeoprocessingValidationException>();
        authoring.ReceivedCalls().Should().BeEmpty();
    }

    [UnitTest]
    public async Task MissingVersion_IsNondisclosingNotFound()
    {
        var authoring = Substitute.For<IWorkflowPackageAuthoring>();
        var invoke = () => new WorkflowPackageTool().InvokeAsync(Context(authoring), JsonSerializer.SerializeToElement(new
        {
            action = "read",
            workspaceId = "workspace-a",
            turnId = "turn-a",
            packageId = "foreign",
            version = 1,
        }), CancellationToken.None);
        await invoke.Should().ThrowAsync<GeoprocessingNotFoundException>();
    }

    private static DefaultHttpContext Context(IWorkflowPackageAuthoring authoring) => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("api_key_id", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            new Claim("permission", "agent:workspace"), new Claim(ClaimTypes.Role, "admin"),
        ], "ApiKey")),
        RequestServices = new ServiceCollection().AddSingleton(authoring).BuildServiceProvider(),
    };
}
