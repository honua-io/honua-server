// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.Operations.Domain;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed partial class PublishedOperationToolTests
{
    [UnitTheory]
    [InlineData("admin.connections.list")]
    [InlineData("admin.connections.tables.discover")]
    [InlineData("admin.connections.layers.list")]
    public async Task Invoke_DiscoveryWithUnsupportedPreview_ReturnsInvalidArgumentWithoutDispatch(string operationId)
    {
        var descriptor = AdminConnectImportOperationCatalog.Descriptors.Single(d => d.OperationId == operationId);
        descriptor.Policy.SupportsDryRun.Should().BeFalse();
        var invoker = new CountingInvoker(_ => CompletedHandle(operationId));
        var tool = new PublishedOperationTool(descriptor, "test", NullLogger.Instance);

        var result = await tool.InvokeAsync(Context(invoker, authorization: ProposalAuthorization(), roles: ["admin"]),
            Args("""{"dryRun":true,"connectionId":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"}"""), default);

        result.IsError.Should().BeTrue();
        result.StructuredContent!.Value.GetProperty("code").GetString().Should().Be(McpErrorMapper.Codes.InvalidArgument);
        result.StructuredContent.Value.GetProperty("message").GetString().Should().Contain("dry-run");
        invoker.SubmitCount.Should().Be(0);
    }

    [UnitTheory]
    [InlineData("admin.connections.list", false)]
    [InlineData("admin.connections.list", true)]
    [InlineData("admin.connections.tables.discover", false)]
    [InlineData("admin.connections.tables.discover", true)]
    [InlineData("admin.connections.layers.list", false)]
    [InlineData("admin.connections.layers.list", true)]
    public async Task Invoke_DiscoveryWithoutPreview_DispatchesNormally(string operationId, bool explicitFalse)
    {
        var descriptor = AdminConnectImportOperationCatalog.Descriptors.Single(d => d.OperationId == operationId);
        var response = operationId switch
        {
            "admin.connections.list" => """{"data":[]}""",
            "admin.connections.tables.discover" => """{"tables":[]}""",
            _ => """{"data":{"items":[],"page":1,"pageSize":20,"total":0}}""",
        };
        var invoker = new CountingInvoker(request =>
        {
            request.DryRun.Should().BeFalse();
            return CompletedHandle(operationId) with
            {
                Result = new OperationResultSummary
                {
                    Summary = "ok",
                    Details = new Dictionary<string, string> { ["response"] = response },
                },
            };
        });
        var tool = new PublishedOperationTool(descriptor, "test", NullLogger.Instance);
        var arguments = explicitFalse
            ? """{"dryRun":false,"connectionId":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"}"""
            : """{"connectionId":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"}""";

        var result = await tool.InvokeAsync(Context(invoker, authorization: ProposalAuthorization(), roles: ["admin"]),
            Args(arguments), default);

        result.IsError.Should().BeFalse();
        invoker.SubmitCount.Should().Be(1);
    }
}
