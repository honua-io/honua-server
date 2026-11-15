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

// A published tool must refuse arguments outside its input schema instead of dropping them:
// a misspelled governed write otherwise seals a no-op proposal and reports it as pending.
public sealed partial class PublishedOperationToolTests
{
    [UnitTheory]
    [InlineData("""{"serviceName":"roads","body":{"allowAnonymousWrite":true}}""", "body")]
    [InlineData("""{"serviceName":"roads","notAField":true}""", "notAField")]
    [InlineData("""{"serviceName":"roads","allowAnonymous":true,"approvedProposalId":"forged"}""", "approvedProposalId")]
    public async Task Invoke_GovernedWriteWithUndeclaredArgument_RejectsWithoutProposal(string json, string unknown)
    {
        const string operationId = "admin.services.access-policy.set";
        var descriptor = AdminApiOperationCatalog.Descriptors.Single(d => d.OperationId == operationId);
        var executor = new RecordingExecutor(operationId);
        var bridge = new CapturingProposalBridge();
        using var catalog = new OperationCatalog([new AdminApiOperationDescriptorProvider()], TimeProvider.System);
        var dispatcher = new OperationDispatcher(
            catalog, [executor],
            new AllowAllPolicyDecisionPoint(), TimeProvider.System, bridge);
        var tool = new PublishedOperationTool(descriptor, "cat-undeclared", NullLogger.Instance);
        var http = Context(dispatcher, authorization: ProposalAuthorization(), roles: ["admin", "agent"]);

        var invoke = () => tool.InvokeAsync(http, Args(json), default);

        var error = await invoke.Should().ThrowAsync<GeoprocessingValidationException>();
        error.Which.Message.Should().Contain(unknown).And.Contain(tool.Name);
        bridge.Calls.Should().Be(0, "an unknown argument must not seal a proposal");
        executor.SubmitCount.Should().Be(0);
    }

    [UnitTest]
    public async Task Invoke_GovernedWriteWithDeclaredArguments_StillProposes()
    {
        const string operationId = "admin.services.access-policy.set";
        var descriptor = AdminApiOperationCatalog.Descriptors.Single(d => d.OperationId == operationId);
        var executor = new RecordingExecutor(operationId);
        var bridge = new CapturingProposalBridge();
        using var catalog = new OperationCatalog([new AdminApiOperationDescriptorProvider()], TimeProvider.System);
        var dispatcher = new OperationDispatcher(
            catalog, [executor],
            new AllowAllPolicyDecisionPoint(), TimeProvider.System, bridge);
        var tool = new PublishedOperationTool(descriptor, "cat-undeclared", NullLogger.Instance);
        var http = Context(dispatcher, authorization: ProposalAuthorization(), roles: ["admin", "agent"]);

        var result = await tool.InvokeAsync(http, Args("""{"serviceName":"roads","allowAnonymousWrite":true}"""), default);

        result.StructuredContent!.Value.GetProperty("status").GetString().Should().Be("RequiresApproval");
        bridge.Calls.Should().Be(1);
        bridge.Request!.Parameters["allowAnonymousWrite"].Should().Be("true");
        executor.SubmitCount.Should().Be(0);
    }

    [UnitTest]
    public async Task Invoke_ReadOnlyWithUndeclaredArgument_RejectsBeforeDispatch()
    {
        var invoker = new CountingInvoker(_ => CompletedHandle(DeterministicReadOnlyOpId));
        var tool = new PublishedOperationTool(DeterministicReadOnlyDescriptor(), "cat-v1", NullLogger.Instance);

        var invoke = () => tool.InvokeAsync(Context(invoker), Args("""{"layerId":"7","layerid":"8","zz":1}"""), default);

        var error = await invoke.Should().ThrowAsync<GeoprocessingValidationException>();
        error.Which.Message.Should().Contain("layerid, zz");
        invoker.SubmitCount.Should().Be(0);
    }

    [UnitTest]
    public async Task Invoke_ManyUndeclaredArguments_ReportsBoundedNames()
    {
        var invoker = new CountingInvoker(_ => CompletedHandle(DeterministicReadOnlyOpId));
        var tool = new PublishedOperationTool(DeterministicReadOnlyDescriptor(), "cat-v1", NullLogger.Instance);
        var extras = string.Join(",", Enumerable.Range(0, 12).Select(i => $"\"x{i:D2}\":1"));
        var longName = new string('n', 200);

        var invoke = () => tool.InvokeAsync(
            Context(invoker), Args($$"""{"layerId":"7",{{extras}},"{{longName}}":1}"""), default);

        var error = await invoke.Should().ThrowAsync<GeoprocessingValidationException>();
        error.Which.Message.Should().Contain("and 3 more").And.NotContain(longName);
        invoker.SubmitCount.Should().Be(0);
    }

    [UnitTest]
    public void Describe_PublishedTool_AdvertisesClosedInputSchema()
    {
        foreach (var descriptor in new[] { DeterministicReadOnlyDescriptor(), MutatingDescriptor() })
        {
            var schema = new PublishedOperationTool(descriptor, "cat-v1", NullLogger.Instance).Describe().InputSchema;
            schema.GetProperty("additionalProperties").ValueKind.Should().Be(System.Text.Json.JsonValueKind.False);
        }
    }

    [UnitTest]
    [Endpoint("POST /mcp tools/call honua_op_geo_summary")]
    public async Task Surface_UndeclaredArgument_ReturnsInvalidArgumentNamingIt()
    {
        var invoker = new CountingInvoker(_ => CompletedHandle(DeterministicReadOnlyOpId));
        var source = new PublishedOperationToolSource(
            Catalog(DeterministicReadOnlyDescriptor()),
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
            Rpc("u1", "tools/call", """{"name":"honua_op_geo_summary","arguments":{"layerId":"7","notAField":true}}"""),
            CancellationToken.None);

        // Tool failures travel inside the result (MCP 2025-03-26) as invalid_argument, the
        // in-result form of JSON-RPC -32602, so stock clients can correct and retry.
        response!.Error.Should().BeNull();
        response.Result!.Value.GetProperty("isError").GetBoolean().Should().BeTrue();
        var body = response.Result!.Value.GetProperty("structuredContent");
        body.GetProperty("code").GetString().Should().Be(McpErrorMapper.Codes.InvalidArgument);
        body.GetProperty("message").GetString().Should().Contain("notAField");
        invoker.SubmitCount.Should().Be(0);
    }
}
