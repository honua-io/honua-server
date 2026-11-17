// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Resources;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

// Every tool call and resource read by an authenticated principal leaves one durable record,
// naming the tool or resource and the target identifiers, never the query text or the result.
[Protocol(TestProtocols.Mcp)]
public sealed class McpAccessAuditTests
{
    private const string LayerAResource = "honua://published-services/layer-a";

    [UnitTest]
    public async Task ToolCallsAndResourceReads_WriteOneRecordEach_WithTargetIdsOnly()
    {
        var auditLog = new RecordingAuditLog();
        var surface = Surface();
        var context = Context(auditLog);

        await DispatchAsync(surface, context, ("tools/call", """{"name":"honua_list_layers","arguments":{}}"""));
        await DispatchAsync(
            surface,
            context,
            ("tools/call",
            """{"name":"honua_query_features","arguments":{"serviceId":"svc-a","serviceLayerIndex":0,"where":"owner = 'secret'"}}"""));
        await DispatchAsync(surface, context, ("resources/read", $$"""{"uri":"{{LayerAResource}}?q=secret"}"""));

        auditLog.Events.Select(e => (e.Action, e.ResourceId, e.Outcome)).Should().Equal(
            (McpAccessAudit.ToolCallAction, "honua_list_layers", AuditOutcome.Success),
            (McpAccessAudit.ToolCallAction, "honua_query_features", AuditOutcome.Success),
            (McpAccessAudit.ResourceReadAction, LayerAResource, AuditOutcome.Success));
        auditLog.Events.Should().OnlyContain(e =>
            e.ResourceType == McpAccessAudit.ResourceType
            && e.EventType == AuditEventType.DataExport
            && e.Actor.Length > 0);
        auditLog.Events[1].Details.Should().Be("""{"targets":{"serviceId":"svc-a","serviceLayerIndex":0}}""");
        string.Concat(auditLog.Events.Select(e => e.Details + e.ResourceId)).Should().NotContain("secret");
    }

    [UnitTest]
    public async Task FailedToolCall_RecordsItsErrorCodeAndOutcome()
    {
        var auditLog = new RecordingAuditLog();

        await DispatchAsync(
            Surface(),
            Context(auditLog),
            ("tools/call",
            """{"name":"honua_query_features","arguments":{"serviceId":"svc-a","fail":"denied"}}"""));

        var record = auditLog.Events.Should().ContainSingle().Which;
        record.Outcome.Should().Be(AuditOutcome.Denied);
        record.Details.Should().Be("""{"targets":{"serviceId":"svc-a"},"code":"permission_denied"}""");
    }

    [UnitTest]
    public async Task GovernedProposalRefusedBeforeItRuns_IsStillRecorded()
    {
        var auditLog = new RecordingAuditLog();
        var surface = new McpDataAccessSurface(
            [new StubProposalTool()], [], NullLogger<McpDataAccessSurface>.Instance);

        var response = await DispatchAsync(
            surface, Context(auditLog), ("tools/call", """{"name":"honua_propose_stub","arguments":{}}"""));

        response.Result!.Value.GetProperty("isError").GetBoolean().Should().BeTrue();
        var record = auditLog.Events.Should().ContainSingle().Which;
        (record.Action, record.ResourceId).Should().Be((McpAccessAudit.ToolCallAction, "honua_propose_stub"));
        record.Outcome.Should().NotBe(AuditOutcome.Success);
    }

    [UnitTest]
    public async Task AnonymousRead_WritesNoAccessRecord()
    {
        var auditLog = new RecordingAuditLog();
        var context = McpTestFactory.AnonymousHttpContext();
        context.RequestServices = Services(auditLog);

        await DispatchAsync(Surface(), context, ("resources/read", $$"""{"uri":"{{LayerAResource}}"}"""));

        auditLog.Events.Should().BeEmpty();
    }

    [UnitTest]
    public async Task AuditFailure_DoesNotFailTheCall()
    {
        var context = Context(new ThrowingAuditLog());

        var response = await DispatchAsync(
            Surface(),
            context,
            ("tools/call",
            """{"name":"honua_list_layers","arguments":{}}"""));

        response.Result!.Value.GetProperty("isError").GetBoolean().Should().BeFalse();
    }

    [UnitTheory]
    [InlineData(null, AuditOutcome.Success)]
    [InlineData("unauthenticated", AuditOutcome.Denied)]
    [InlineData("insufficient_scope", AuditOutcome.Denied)]
    [InlineData("not_found", AuditOutcome.Failure)]
    public void Outcome_MapsErrorCodes(string? code, AuditOutcome expected) =>
        McpAccessAudit.Outcome(code).Should().Be(expected);

    [UnitTest]
    public void ToolDetails_CapsLongIdentifiersAndIgnoresNonIdentifierValues()
    {
        using var arguments = JsonDocument.Parse(
            $$"""{"serviceId":"{{new string('s', 300)}}","jobId":{"nested":true},"filter":"x"}""");

        var details = McpAccessAudit.ToolDetails(arguments.RootElement, errorCode: null);

        details.Should().Be($$$"""{"targets":{"serviceId":"{{{new string('s', 128)}}}"}}""");
    }

    private static McpDataAccessSurface Surface() =>
        new(
            [new StubTool("honua_list_layers"), new StubTool("honua_query_features")],
            [new StubResource()],
            NullLogger<McpDataAccessSurface>.Instance);

    private static DefaultHttpContext Context(IAuditLog auditLog) =>
        McpTestFactory.AuthenticatedHttpContextWithServices(services => services.AddSingleton(auditLog));

    private static ServiceProvider Services(IAuditLog auditLog) =>
        new ServiceCollection().AddSingleton(auditLog).BuildServiceProvider();

    private static async Task<McpJsonRpcResponse> DispatchAsync(
        McpDataAccessSurface surface,
        HttpContext context,
        (string Method, string Parameters) call)
    {
        using var document = JsonDocument.Parse(call.Parameters);
        var request = new McpJsonRpcRequest
        {
            JsonRpc = "2.0",
            Id = JsonDocument.Parse("\"a1\"").RootElement.Clone(),
            Method = call.Method,
            Params = document.RootElement.Clone(),
        };

        return (await surface.DispatchAsync(context, request, CancellationToken.None))!;
    }

    private sealed class RecordingAuditLog : IAuditLog
    {
        public List<AuditEvent> Events { get; } = [];

        public Task<string?> RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(auditEvent);
            return Task.FromResult<string?>("audit-1");
        }
    }

    private sealed class ThrowingAuditLog : IAuditLog
    {
        public Task<string?> RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("audit store unavailable");
    }

    private sealed class StubTool(string name) : IMcpTool
    {
        public string Name => name;

        public string WorkflowFamily => McpTelemetry.WorkflowFamily.Results;

        public McpToolDescriptor Describe() =>
            new() { Name = name, InputSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone() };

        public Task<McpToolsCallResult> InvokeAsync(
            HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken)
        {
            if (arguments is { ValueKind: JsonValueKind.Object } supplied && supplied.TryGetProperty("fail", out _))
            {
                throw new GeoprocessingAuthorizationException(requiresAuthentication: false, "denied");
            }

            return Task.FromResult(McpToolHelpers.SuccessJsonElement(JsonDocument.Parse("{}").RootElement.Clone()));
        }
    }

    // Without a proposal-evidence verifier the surface refuses this tool before invoking it.
    private sealed class StubProposalTool : IMcpTool, IEvidenceBoundProposalTool
    {
        public string Name => "honua_propose_stub";

        public string OperationId => "stub.propose";

        public string TargetProperty => "targetId";

        public string WorkflowFamily => McpTelemetry.WorkflowFamily.Results;

        public McpToolDescriptor Describe() =>
            new() { Name = Name, InputSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone() };

        public Task<McpToolsCallResult> InvokeAsync(
            HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a refused governed call must not run");
    }

    private sealed class StubResource : IMcpResource
    {
        public string Family => "published-services";

        public IReadOnlyList<McpResourceDescriptor> Describe() => [];

        public IReadOnlyList<McpResourceTemplateDescriptor> DescribeTemplates() => [];

        public bool CanHandle(string uri) => uri.StartsWith("honua://published-services", StringComparison.Ordinal);

        public Task<McpResourcesReadResult> ReadAsync(HttpContext httpContext, string uri, CancellationToken cancellationToken) =>
            Task.FromResult(new McpResourcesReadResult
            {
                Contents = [new McpResourceContent { Uri = uri, Text = "{}" }],
            });
    }
}
