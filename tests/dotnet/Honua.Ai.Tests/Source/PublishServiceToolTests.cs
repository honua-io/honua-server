// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Verifies the MCP <c>honua_publish_service</c> tool (#1951): it advertises the
/// correct write annotations and output schema, routes <c>tools/call</c> through
/// the canonical <see cref="IOperationInvoker"/> (operations toolset /
/// service.publish), and projects the resulting <see cref="OperationHandle"/>
/// into a structured Completed / RequiresApproval / unavailable result.
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class PublishServiceToolTests
{
    private static DefaultHttpContext ContextWithInvoker(IOperationInvoker? invoker)
    {
        var services = new ServiceCollection();
        if (invoker is not null)
        {
            services.AddSingleton(invoker);
        }

        return new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "agent-x")], "Test"))
        };
    }

    private static PublishServiceTool CreateTool()
        => new(NullLogger<PublishServiceTool>.Instance);

    private static System.Text.Json.JsonElement Arguments(McpPublishServiceArgument argument)
        => McpTestFactory.ToArguments(argument, McpJsonContext.Default.McpPublishServiceArgument);

    [UnitTest]
    public void Describe_AdvertisesWriteAnnotationsAndOutputSchema()
    {
        var descriptor = CreateTool().Describe();

        descriptor.Name.Should().Be("honua_publish_service");
        descriptor.Title.Should().NotBeNullOrWhiteSpace();
        descriptor.Annotations.Should().NotBeNull();
        descriptor.Annotations!.ReadOnlyHint.Should().BeFalse("publishing mutates the catalog");
        descriptor.Annotations.DestructiveHint.Should().BeFalse("publish creates a layer rather than destroying state");
        descriptor.Annotations.IdempotentHint.Should().BeFalse("service.publish does not honor an idempotency key");

        descriptor.OutputSchema.Should().NotBeNull();
        var schema = descriptor.OutputSchema!.Value;
        schema.GetProperty("type").GetString().Should().Be("object");
        schema.GetProperty("properties").TryGetProperty("status", out _).Should().BeTrue();
        schema.GetProperty("properties").TryGetProperty("serviceUri", out _).Should().BeTrue();
        schema.GetProperty("properties").TryGetProperty("metadataRevision", out _).Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_publish_service")]
    public async Task PublishService_WhenCompleted_RoutesToInvoker_AndReturnsServiceUriAndRevision()
    {
        OperationRequest? captured = null;
        var invoker = new FakeInvoker((request, _) =>
        {
            captured = request;
            return new OperationHandle
            {
                OperationId = PublishServiceTool.PublishOperationId,
                HandleId = "op-abc",
                Status = OperationHandleStatus.Completed,
                MetadataRevision = 42,
                Result = new OperationResultSummary
                {
                    Summary = "Published layer 'Parcels' (id 7) to service 'cadastre'.",
                    Details = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["layerId"] = "7",
                        ["serviceName"] = "cadastre",
                    }
                }
            };
        });

        var tool = CreateTool();
        var arguments = Arguments(new McpPublishServiceArgument
        {
            ConnectionId = "11111111-1111-1111-1111-111111111111",
            Schema = "public",
            Table = "parcels",
            LayerName = "Parcels",
            ServiceName = "cadastre",
            Srid = 4326,
            Fields = ["objectid", "owner"]
        });

        var result = await tool.InvokeAsync(ContextWithInvoker(invoker), arguments, CancellationToken.None);

        result.IsError.Should().BeFalse();
        var content = result.StructuredContent!.Value;
        content.GetProperty("status").GetString().Should().Be("Completed");
        content.GetProperty("requiresApproval").GetBoolean().Should().BeFalse();
        content.GetProperty("serviceUri").GetString().Should().Be("honua://published-services/cadastre");
        content.GetProperty("layerId").GetString().Should().Be("7");
        content.GetProperty("metadataRevision").GetInt64().Should().Be(42);

        // The tool adapts onto the canonical service.publish operation rather than
        // reimplementing publishing.
        captured.Should().NotBeNull();
        captured!.OperationId.Should().Be("service.publish");
        captured.ConnectionId.Should().Be("11111111-1111-1111-1111-111111111111");
        captured.ServiceName.Should().Be("cadastre");
        captured.Parameters["schema"].Should().Be("public");
        captured.Parameters["table"].Should().Be("parcels");
        captured.Parameters["layerName"].Should().Be("Parcels");
        captured.Parameters["srid"].Should().Be("4326");
        captured.Fields.Should().BeEquivalentTo("objectid", "owner");
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_publish_service")]
    public async Task PublishService_WhenRequiresApproval_ReturnsApprovalLane()
    {
        var invoker = new FakeInvoker((_, _) => new OperationHandle
        {
            OperationId = PublishServiceTool.PublishOperationId,
            HandleId = "op-pending",
            Status = OperationHandleStatus.RequiresApproval,
            ApprovalLane = "operator-gate",
            Reason = "Publishing requires operator approval on this tier."
        });

        var tool = CreateTool();
        var arguments = Arguments(new McpPublishServiceArgument
        {
            ConnectionId = "conn-1",
            Schema = "public",
            Table = "parcels",
            LayerName = "Parcels"
        });

        var result = await tool.InvokeAsync(ContextWithInvoker(invoker), arguments, CancellationToken.None);

        var content = result.StructuredContent!.Value;
        content.GetProperty("status").GetString().Should().Be("RequiresApproval");
        content.GetProperty("requiresApproval").GetBoolean().Should().BeTrue();
        content.GetProperty("approvalLane").GetString().Should().Be("operator-gate");
        content.GetProperty("message").GetString().Should().Be("Publishing requires operator approval on this tier.");
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_publish_service")]
    public async Task PublishService_WhenInvokerUnavailable_ReturnsFailedWithoutThrowing()
    {
        var tool = CreateTool();
        var arguments = Arguments(new McpPublishServiceArgument
        {
            ConnectionId = "conn-1",
            Schema = "public",
            Table = "parcels",
            LayerName = "Parcels"
        });

        var result = await tool.InvokeAsync(ContextWithInvoker(invoker: null), arguments, CancellationToken.None);

        result.IsError.Should().BeFalse();
        var content = result.StructuredContent!.Value;
        content.GetProperty("status").GetString().Should().Be("Failed");
        content.GetProperty("requiresApproval").GetBoolean().Should().BeFalse();
        content.GetProperty("message").GetString().Should().Contain("operations toolset is unavailable");
    }

    private sealed class FakeInvoker(Func<OperationRequest, OperationPolicyContext, OperationHandle> handler)
        : IOperationInvoker
    {
        public Task<OperationValidation> ValidateAsync(OperationRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new OperationValidation { IsValid = true, Status = "valid" });

        public Task<OperationHandle> SubmitAsync(
            OperationRequest request,
            OperationPolicyContext context,
            CancellationToken cancellationToken = default)
            => Task.FromResult(handler(request, context));
    }
}
