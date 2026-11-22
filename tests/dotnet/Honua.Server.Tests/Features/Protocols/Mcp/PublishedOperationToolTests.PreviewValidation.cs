// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Services;
using Honua.Geoprocessing;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed partial class PublishedOperationToolTests
{
    [UnitTheory]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("\"preview-please\"")]
    [InlineData("\"\"")]
    public async Task Invoke_InvalidPreviewFlag_RejectsBeforeDispatch(string value)
    {
        var descriptor = ApprovalFreeMutatingDescriptor();
        var invoker = new CountingInvoker(_ => CompletedHandle(descriptor.OperationId));
        var tool = new PublishedOperationTool(descriptor, "cat-v1", NullLogger.Instance);
        var invoke = () => tool.InvokeAsync(Context(invoker), Args("{\"layerId\":\"7\",\"dryRun\":" + value + "}"), default);

        var error = await invoke.Should().ThrowAsync<GeoprocessingValidationException>();

        error.Which.Message.Should().Contain("dryRun").And.NotContain("preview-please");
        invoker.SubmitCount.Should().Be(0);
    }

    [UnitTheory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("\"true\"", true)]
    [InlineData("\"false\"", false)]
    public async Task Invoke_ValidPreviewFlag_PreservesBooleanAndLegacyString(string value, bool expected)
    {
        var descriptor = ApprovalFreeMutatingDescriptor();
        var invoker = new CountingInvoker(request =>
        {
            request.DryRun.Should().Be(expected);
            return CompletedHandle(descriptor.OperationId);
        });
        var tool = new PublishedOperationTool(descriptor, "cat-v1", NullLogger.Instance);

        var result = await tool.InvokeAsync(Context(invoker), Args("{\"layerId\":\"7\",\"dryRun\":" + value + "}"), default);

        result.IsError.Should().BeFalse();
        invoker.SubmitCount.Should().Be(1);
    }

    [UnitTest]
    [Endpoint("POST /mcp tools/call honua_op_geo_summary")]
    public async Task Surface_InvalidPreviewFlag_ReturnsInvalidArgumentWithoutDispatch()
    {
        var original = DeterministicReadOnlyDescriptor();
        var descriptor = original with { Policy = original.Policy with { SupportsDryRun = true } };
        var invoker = new CountingInvoker(_ => CompletedHandle(descriptor.OperationId));
        var source = new PublishedOperationToolSource(
            Catalog(descriptor),
            Options.Create(new McpPublishedOperationOptions { Enabled = true }),
            NullLogger<PublishedOperationToolSource>.Instance);
        var surface = new McpDataAccessSurface(
            [], [], NullLogger<McpDataAccessSurface>.Instance, limits: null, toolSources: [source]);
        using var cache = new PublishedOperationCache();
        using var services = new ServiceCollection().AddSingleton<IOperationInvoker>(invoker)
            .AddSingleton<IPublishedOperationCache>(cache)
            .BuildServiceProvider();

        var response = await surface.DispatchAsync(
            AuthenticatedContext(services),
            Rpc("preview", "tools/call", """{"name":"honua_op_geo_summary","arguments":{"layerId":"7","dryRun":"preview-please"}}"""),
            CancellationToken.None);

        response!.Error.Should().BeNull();
        response.Result!.Value.GetProperty("isError").GetBoolean().Should().BeTrue();
        var body = response.Result.Value.GetProperty("structuredContent");
        body.GetProperty("code").GetString().Should().Be(McpErrorMapper.Codes.InvalidArgument);
        body.GetProperty("message").GetString().Should().Contain("dryRun").And.NotContain("preview-please");
        invoker.SubmitCount.Should().Be(0);
    }
}
