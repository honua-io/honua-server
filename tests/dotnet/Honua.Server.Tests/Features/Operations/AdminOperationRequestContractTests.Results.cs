// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Operations.Domain;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin.Models;
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
        var payload = SerializePublishedLayer(layerId);
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
            ["storageLayerId"] = layerId.ToString(System.Globalization.CultureInfo.InvariantCulture),
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
    [InlineData("storageLayerId", "-1")]
    [InlineData("storageLayerId", "1.5")]
    [InlineData("storageLayerId", "2147483648")]
    [InlineData("storageLayerId", "\"1\"")]
    [InlineData("storageLayerId", "null")]
    [InlineData("serviceName", "\" \"")]
    [InlineData("serviceName", "null")]
    [InlineData("serviceName", "7")]
    [InlineData("success", "false")]
    public async Task SubmitAsync_InvalidPublishedIdentity_DoesNotInventResourceIds(string field, string value)
    {
        var envelope = JsonNode.Parse(SerializePublishedLayer(42))!;
        var target = field == "success" ? envelope : envelope["data"]!;
        target[field] = JsonNode.Parse(value);
        var payload = envelope.ToJsonString();
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
    [InlineData("admin.layer.set-enabled", HttpStatusCode.Created, false)]
    public async Task SubmitAsync_WithoutCommittedPublication_DoesNotInventResourceIdentity(
        string operationId, HttpStatusCode statusCode, bool dryRun)
    {
        var payload = SerializePublishedLayer(42);
        using var capture = new CaptureHandler(payload, statusCode);
        using var client = new HttpClient(capture);
        var executor = CreateApi(operationId, client);

        var handle = await executor.SubmitAsync(new OperationRequest
        {
            OperationId = operationId,
            ConnectionId = "11111111-1111-1111-1111-111111111111",
            Parameters = new Dictionary<string, string?> { ["storageLayerId"] = "42" },
            DryRun = dryRun,
        }, new OperationPolicyContext());

        handle.Status.Should().Be(statusCode == HttpStatusCode.BadRequest
            ? OperationHandleStatus.Failed
            : OperationHandleStatus.Completed);
        handle.ResourceIds.Should().BeEmpty();
    }

    private static string SerializePublishedLayer(int layerId)
        => JsonSerializer.Serialize(ApiResponse<PublishedLayerSummary>.CreateSuccess(new PublishedLayerSummary
        {
            StorageLayerId = layerId,
            LayerName = "Roads",
            Schema = "public",
            Table = "roads",
            GeometryType = "Point",
            ServiceName = "published-roads",
        }), LayerPublishingJsonContext.Default.ApiResponsePublishedLayerSummary);
}
