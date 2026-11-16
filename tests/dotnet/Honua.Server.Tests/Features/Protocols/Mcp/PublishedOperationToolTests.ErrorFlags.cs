// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.Operations.Domain;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed partial class PublishedOperationToolTests
{
    [UnitTheory]
    [InlineData(OperationHandleStatus.Failed, true)]
    [InlineData(OperationHandleStatus.Completed, false)]
    [InlineData(OperationHandleStatus.Accepted, false)]
    [InlineData(OperationHandleStatus.Queued, false)]
    [InlineData(OperationHandleStatus.Running, false)]
    [InlineData(OperationHandleStatus.RequiresApproval, false)]
    [InlineData(OperationHandleStatus.DryRunRequired, false)]
    public async Task Invoke_CanonicalOutcome_PreservesEnvelopeAndReportsFailureToClient(
        OperationHandleStatus status, bool isError)
    {
        var handle = CompletedHandle(DeterministicReadOnlyOpId) with
        {
            Status = status,
            Reason = "Canonical operation outcome.",
        };
        var invoker = new CountingInvoker(_ => handle);
        var tool = new PublishedOperationTool(DeterministicReadOnlyDescriptor(), "cat-v1", NullLogger.Instance);

        var result = await tool.InvokeAsync(Context(invoker), Args("""{"layerId":"7"}"""), default);

        result.IsError.Should().Be(isError);
        var body = result.StructuredContent!.Value;
        body.GetProperty("status").GetString().Should().Be(status.ToString());
        body.GetProperty("operationInstanceId").GetString().Should().Be(handle.OperationInstanceId);
        body.GetProperty("message").GetString().Should().Be(handle.Reason);
        body.GetProperty("requiresApproval").GetBoolean()
            .Should().Be(status == OperationHandleStatus.RequiresApproval);
        var text = result.Content.Last(block => block.Type == "text").Text!;
        JsonNode.DeepEquals(JsonNode.Parse(text), JsonNode.Parse(body.GetRawText())).Should().BeTrue();
        invoker.SubmitCount.Should().Be(1);
    }
}
