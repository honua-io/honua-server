// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Operations.Domain;
using Honua.TestKit.Attributes;
using Xunit;

namespace Honua.Server.Tests.Features.OperationsToolset;

public sealed partial class AdminOperationRequestContractTests
{
    [UnitTheory]
    [InlineData(0)]
    [InlineData(42)]
    public async Task SubmitAsync_PublishedLayer_ReportsCreatedResourceIdentity(int layerId)
    {
        var payload = JsonSerializer.Serialize(new { success = true, data = new { layerId, serviceName = "published-roads" } });
        using var capture = new CaptureHandler(payload, HttpStatusCode.Created);
        using var client = new HttpClient(capture);
        var executor = CreateApi("admin.layer.publish", client);

        var handle = await executor.SubmitAsync(new OperationRequest
        {
            OperationId = executor.OperationId,
            ConnectionId = "11111111-1111-1111-1111-111111111111",
            ServiceName = "requested-service",
        }, new OperationPolicyContext());

        handle.Status.Should().Be(OperationHandleStatus.Completed);
        handle.ResourceIds.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["serviceName"] = "published-roads",
            ["layerId"] = layerId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        handle.Result!.Details["response"].Should().Be(payload);
        (await executor.GetStatusAsync(handle)).ResourceIds.Should().BeEquivalentTo(handle.ResourceIds);
    }

    [UnitTheory]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"data\":[]}")]
    [InlineData("{\"data\":null}")]
    [InlineData("{\"data\":{\"layerId\":1}}")]
    [InlineData("{\"data\":{\"layerId\":1,\"serviceName\":\" \"}}")]
    [InlineData("{\"data\":{\"layerId\":\"1\",\"serviceName\":\"roads\"}}")]
    [InlineData("{\"data\":{\"layerId\":-1,\"serviceName\":\"roads\"}}")]
    [InlineData("{\"data\":{\"layerId\":1.5,\"serviceName\":\"roads\"}}")]
    [InlineData("{\"data\":{\"layerId\":2147483648,\"serviceName\":\"roads\"}}")]
    [InlineData("{\"data\":{\"layerId\":1,\"serviceName\":7}}")]
    public async Task SubmitAsync_UnusablePublishResponse_PreservesCompletionWithoutResourceIds(string payload)
    {
        using var capture = new CaptureHandler(payload, HttpStatusCode.Created);
        using var client = new HttpClient(capture);
        var executor = CreateApi("admin.layer.publish", client);

        var handle = await executor.SubmitAsync(new OperationRequest
        {
            OperationId = executor.OperationId,
            ConnectionId = "11111111-1111-1111-1111-111111111111",
            ServiceName = "requested-service",
        }, new OperationPolicyContext());

        handle.Status.Should().Be(OperationHandleStatus.Completed);
        handle.ResourceIds.Should().BeEmpty();
        handle.Result!.Details["response"].Should().Be(payload);
    }

    [UnitTheory]
    [InlineData("admin.layer.publish", HttpStatusCode.Created, true)]
    [InlineData("admin.layer.publish", HttpStatusCode.Accepted, false)]
    [InlineData("admin.layer.publish", HttpStatusCode.BadRequest, false)]
    [InlineData("admin.layer.set-enabled", HttpStatusCode.OK, false)]
    public async Task SubmitAsync_WithoutCommittedPublication_DoesNotInventResourceIdentity(
        string operationId, HttpStatusCode statusCode, bool dryRun)
    {
        const string payload = """{"data":{"layerId":42,"serviceName":"roads"}}""";
        using var capture = new CaptureHandler(payload, statusCode);
        using var client = new HttpClient(capture);
        var executor = CreateApi(operationId, client);

        var handle = await executor.SubmitAsync(new OperationRequest
        {
            OperationId = operationId,
            ConnectionId = "11111111-1111-1111-1111-111111111111",
            Parameters = new Dictionary<string, string?> { ["layerId"] = "42" },
            DryRun = dryRun,
        }, new OperationPolicyContext());

        handle.Status.Should().Be(statusCode == HttpStatusCode.BadRequest
            ? OperationHandleStatus.Failed
            : OperationHandleStatus.Completed);
        handle.ResourceIds.Should().BeEmpty();
    }
}
