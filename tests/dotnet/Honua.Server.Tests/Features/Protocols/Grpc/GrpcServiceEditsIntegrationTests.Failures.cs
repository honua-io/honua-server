// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

public sealed partial class GrpcServiceEditsIntegrationTests
{
    [IntegrationTheory]
    [InlineData(ServiceFailurePolicy.Rollback)]
    [InlineData(ServiceFailurePolicy.KeepSuccessfulRows)]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyServiceEdits")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/ApplyServiceEdits")]
    public async Task ApplyServiceEdits_SecondLayerConstraintFailure_ObeysRollbackAcrossLayers(ServiceFailurePolicy policy)
    {
        await RejectSecondLayerAddAsync();
        var before = await _fixture.CountStoredFeaturesAsync(0);
        var request = Request();
        var rollback = policy == ServiceFailurePolicy.Rollback;
        request.RollbackOnFailure = rollback;

        var response = await _client!.ApplyServiceEditsAsync(request, _headers);

        response.Results.Select(result => result.LayerId).Should().Equal(0, 1);
        response.Results[0].Results.AddResults[0].Success.Should().Be(!rollback);
        response.Results[1].Results.AddResults[0].Success.Should().BeFalse();
        (response.Error is not null).Should().Be(rollback);
        (await _fixture.CountStoredFeaturesAsync(0)).Should().Be(before + (rollback ? 0 : 1));
        (await _fixture.CountStoredFeaturesByNameAsync(1, "service edit")).Should().Be(0);
    }

    [IntegrationTest]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyServiceEdits")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/ApplyServiceEdits")]
    public async Task ApplyServiceEdits_SameKey_ReplaysCompleteOrderedResultAndRejectsChangedPayload()
    {
        var request = Request();
        request.IdempotencyKey = Guid.NewGuid().ToString("N");
        var first = await _client!.ApplyServiceEditsAsync(request, _headers);
        var retry = await ReplayOnNewNodeAsync(request);

        retry.Should().Be(first);
        foreach (var layer in new[] { 0, 1 })
        {
            (await _fixture.CountStoredFeaturesByNameAsync(layer, "service edit")).Should().Be(1);
        }
        request.Edits[1].Adds[0].Attributes["name"].StringValue = "changed payload";
        var act = async () => await _client.ApplyServiceEditsAsync(request, _headers);
        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        (await _fixture.CountStoredFeaturesByNameAsync(1, "changed payload")).Should().Be(0);
    }

    [IntegrationTest]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyServiceEdits")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/ApplyServiceEdits")]
    public async Task ApplyServiceEdits_MissingSecondLayerTarget_FailsBeforeFirstLayerWrites()
    {
        var before = await _fixture.CountStoredFeaturesAsync(0);
        var request = Request();
        request.Edits[1].Adds.Clear();
        request.Edits[1].Deletes.Add(long.MaxValue);

        var act = async () => await _client!.ApplyServiceEditsAsync(request, _headers);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.NotFound);
        (await _fixture.CountStoredFeaturesAsync(0)).Should().Be(before);
    }

    [IntegrationTheory]
    [InlineData(ServiceFailurePolicy.Rollback)]
    [InlineData(ServiceFailurePolicy.KeepSuccessfulRows)]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyServiceEdits")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/ApplyServiceEdits")]
    public async Task ApplyServiceEdits_KeyedFailure_NewNodeReplaysAfterConstraintIsRemoved(ServiceFailurePolicy policy)
    {
        await RejectSecondLayerAddAsync();
        var request = Request();
        var rollback = policy == ServiceFailurePolicy.Rollback;
        request.RollbackOnFailure = rollback;
        request.IdempotencyKey = Guid.NewGuid().ToString("N");
        var first = await _client!.ApplyServiceEditsAsync(request, _headers);
        var count = await _fixture.CountStoredFeaturesAsync(0);
        await using var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema!);
        await using var command = connection.CreateCommand();
        command.CommandText = "ALTER TABLE features DROP CONSTRAINT reject_service_test_add";
        await command.ExecuteNonQueryAsync();

        var retry = await ReplayOnNewNodeAsync(request);

        retry.Should().Be(first);
        retry.Results[1].Results.AddResults[0].Success.Should().BeFalse();
        (await _fixture.CountStoredFeaturesAsync(0)).Should().Be(count);
        (await _fixture.CountStoredFeaturesByNameAsync(1, "service edit")).Should().Be(0);
    }

    public enum ServiceFailurePolicy { Rollback, KeepSuccessfulRows }

    [IntegrationTest]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyServiceEdits")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/ApplyServiceEdits")]
    public async Task ApplyServiceEdits_ConcurrentNodesSameKey_CommitEachLayerOnce()
    {
        var node = new WebAppFixture().WithTestLicense(HonuaEdition.Community).ShareSchemaWith(_fixture);
        await node.InitializeAsync();
        try
        {
            using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
            {
                HttpHandler = new GrpcWebHandler(GrpcWebMode.GrpcWeb, node.CreateHandler())
            });
            var other = new Proto.FeatureService.FeatureServiceClient(channel);
            var request = Request();
            request.IdempotencyKey = Guid.NewGuid().ToString("N");

            var responses = await Task.WhenAll(
                _client!.ApplyServiceEditsAsync(request, _headers).ResponseAsync,
                other.ApplyServiceEditsAsync(request.Clone(), _headers).ResponseAsync);

            responses[0].Should().Be(responses[1]);
            responses[0].Error.Should().BeNull();
            foreach (var layer in new[] { 0, 1 })
            {
                (await _fixture.CountStoredFeaturesByNameAsync(layer, "service edit")).Should().Be(1);
            }
        }
        finally
        {
            await node.DisposeAsync();
        }
    }

    private async Task<Proto.ApplyServiceEditsResponse> ReplayOnNewNodeAsync(Proto.ApplyServiceEditsRequest request)
    {
        var node = new WebAppFixture().WithTestLicense(HonuaEdition.Community).ShareSchemaWith(_fixture);
        await node.InitializeAsync();
        try
        {
            using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
            {
                HttpHandler = new GrpcWebHandler(GrpcWebMode.GrpcWeb, node.CreateHandler())
            });
            return await new Proto.FeatureService.FeatureServiceClient(channel).ApplyServiceEditsAsync(request, _headers);
        }
        finally
        {
            await node.DisposeAsync();
        }
    }

    private async Task RejectSecondLayerAddAsync()
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema!);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            ALTER TABLE features ADD CONSTRAINT reject_service_test_add
            CHECK (layer_id <> 1 OR attributes->>'name' IS DISTINCT FROM 'service edit');
            """;
        await command.ExecuteNonQueryAsync();
    }
}
