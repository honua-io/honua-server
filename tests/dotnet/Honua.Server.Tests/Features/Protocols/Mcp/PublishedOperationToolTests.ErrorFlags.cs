// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.Operations.Domain;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed partial class PublishedOperationToolTests
{
    [UnitTest]
    public async Task Invoke_FailedCanonicalOutcome_ReportsErrorToClient()
    {
        var result = await InvokeAndAssertCanonicalEnvelopeAsync(OperationHandleStatus.Failed);

        result.IsError.Should().BeTrue();
    }

    [UnitTheory]
    [InlineData(OperationHandleStatus.Completed)]
    [InlineData(OperationHandleStatus.Accepted)]
    [InlineData(OperationHandleStatus.Queued)]
    [InlineData(OperationHandleStatus.Running)]
    [InlineData(OperationHandleStatus.RequiresApproval)]
    [InlineData(OperationHandleStatus.DryRunRequired)]
    public async Task Invoke_NonFailedCanonicalOutcome_ReportsSuccessToClient(OperationHandleStatus status)
    {
        var result = await InvokeAndAssertCanonicalEnvelopeAsync(status);

        result.IsError.Should().BeFalse();
    }

    private static async Task<McpToolsCallResult> InvokeAndAssertCanonicalEnvelopeAsync(OperationHandleStatus status)
    {
        var handle = CompletedHandle(DeterministicReadOnlyOpId) with
        {
            Status = status,
            Reason = "Canonical operation outcome.",
        };
        var invoker = new CountingInvoker(_ => handle);
        var tool = new PublishedOperationTool(DeterministicReadOnlyDescriptor(), "cat-v1", NullLogger.Instance);

        var result = await tool.InvokeAsync(Context(invoker), Args("""{"layerId":"7"}"""), default);

        var body = result.StructuredContent!.Value;
        body.GetProperty("status").GetString().Should().Be(status.ToString());
        body.GetProperty("operationInstanceId").GetString().Should().Be(handle.OperationInstanceId);
        body.GetProperty("message").GetString().Should().Be(handle.Reason);
        body.GetProperty("requiresApproval").GetBoolean()
            .Should().Be(status == OperationHandleStatus.RequiresApproval);
        var text = result.Content.Last(block => block.Type == "text").Text!;
        JsonNode.DeepEquals(JsonNode.Parse(text), JsonNode.Parse(body.GetRawText())).Should().BeTrue();
        invoker.SubmitCount.Should().Be(1);
        return result;
    }
}
