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

[Collection("Database")]
[Protocol(TestProtocols.Grpc)]
[Operation(Operations.ApplyEdits)]
public sealed partial class GrpcServiceEditsIntegrationTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .WithTestLicense(HonuaEdition.Community)
        .OverrideConfiguration(new Dictionary<string, string?> { ["Limits:Edits:MaxFeaturesPerEdit"] = "2" });
    private GrpcChannel? _channel;
    private Proto.FeatureService.FeatureServiceClient? _client;
    private Metadata? _headers;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = new GrpcWebHandler(GrpcWebMode.GrpcWeb, _fixture.CreateHandler())
        });
        _client = new Proto.FeatureService.FeatureServiceClient(_channel);
        _headers = new Metadata();
        if (_fixture.CurrentSchema is not null)
        {
            _headers.Add("X-Honua-Test-Schema", _fixture.CurrentSchema);
        }
    }

    public async Task DisposeAsync()
    {
        _channel?.Dispose();
        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyServiceEdits")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/ApplyServiceEdits")]
    public async Task ApplyServiceEdits_GrpcWebTwoLayers_CommitsInRequestOrder()
    {
        var request = Request();
        var response = await _client!.ApplyServiceEditsAsync(request, _headers);
        response.Error.Should().BeNull();
        response.Results.Select(result => result.LayerId).Should().Equal(0, 1);
        foreach (var layer in response.Results)
        {
            var added = layer.Results.AddResults.Should().ContainSingle().Subject;
            added.Success.Should().BeTrue();
            (await _fixture.ReadStoredFeatureNameAsync(layer.LayerId, added.ObjectId)).Should().Be("service edit");
        }
    }

    [IntegrationTest]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyServiceEdits")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/ApplyServiceEdits")]
    public async Task ApplyServiceEdits_InvalidSecondLayer_CommitsNothing()
    {
        var before = await _fixture.CountStoredFeaturesAsync(0);
        var request = Request();
        request.Edits[1].LayerId = int.MaxValue;
        var act = async () => await _client!.ApplyServiceEditsAsync(request, _headers);
        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.NotFound);
        (await _fixture.CountStoredFeaturesAsync(0)).Should().Be(before);
    }

    [IntegrationTest]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyServiceEdits")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/ApplyServiceEdits")]
    public async Task ApplyServiceEdits_DuplicateLayer_FailsBeforeWrites()
    {
        var before = await _fixture.CountStoredFeaturesAsync(0);
        var request = Request();
        request.Edits[1].LayerId = 0;
        var act = async () => await _client!.ApplyServiceEditsAsync(request, _headers);
        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        (await _fixture.CountStoredFeaturesAsync(0)).Should().Be(before);
    }

    [IntegrationTest]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyServiceEdits")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/ApplyServiceEdits")]
    public async Task ApplyServiceEdits_CombinedLayersExceedLimit_FailsBeforeWrites()
    {
        var before = await _fixture.CountStoredFeaturesAsync(0);
        var request = Request();
        request.Edits[1].Adds.Add(request.Edits[1].Adds[0].Clone());

        var act = async () => await _client!.ApplyServiceEditsAsync(request, _headers);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        (await _fixture.CountStoredFeaturesAsync(0)).Should().Be(before);
        (await _fixture.CountStoredFeaturesByNameAsync(1, "service edit")).Should().Be(0);
    }

    [IntegrationTest]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyServiceEdits")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/ApplyServiceEdits")]
    public async Task ApplyServiceEdits_InvalidSecondLayerAttributes_FailsBeforeWrites()
    {
        var before = await _fixture.CountStoredFeaturesAsync(0);
        var request = Request();
        request.Edits[1].Adds[0].Attributes["unknown_service_edit_field"] =
            new Proto.AttributeValue { StringValue = "invalid" };

        var act = async () => await _client!.ApplyServiceEditsAsync(request, _headers);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        (await _fixture.CountStoredFeaturesAsync(0)).Should().Be(before);
        (await _fixture.CountStoredFeaturesByNameAsync(1, "service edit")).Should().Be(0);
    }

    private static Proto.ApplyServiceEditsRequest Request()
    {
        var request = new Proto.ApplyServiceEditsRequest { ServiceId = WebAppFixture.TestServiceId, RollbackOnFailure = true };
        foreach (var id in new[] { 0, 1 })
        {
            request.Edits.Add(new Proto.ServiceLayerEdits
            {
                LayerId = id,
                Adds = { new Proto.Feature
                {
                    Attributes = { ["name"] = new Proto.AttributeValue { StringValue = "service edit" } },
                    Geometry = new Proto.Geometry { Point = new Proto.PointGeometry { X = -157.85, Y = 21.30 } }
                } }
            });
        }
        return request;
    }
}
