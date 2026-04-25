// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

/// <summary>
/// Integration tests for gRPC FeatureService exercised through the full ASP.NET Core pipeline
/// with a real PostgreSQL database via <see cref="WebAppFixture"/>.
/// Uses gRPC-Web transport (HTTP/1.1) since the in-memory test server does not support HTTP/2.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Grpc)]
public sealed class GrpcIntegrationTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private GrpcChannel? _channel;
    private Proto.FeatureService.FeatureServiceClient? _client;
    private Metadata? _headers;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();

        // Use GrpcWebHandler wrapping the test server's handler.
        // This converts gRPC to gRPC-Web format (HTTP/1.1), and the server's
        // UseGrpcWeb middleware (DefaultEnabled = true) converts back to native gRPC.
        var testServerHandler = _fixture.CreateHandler();
        var grpcWebHandler = new GrpcWebHandler(GrpcWebMode.GrpcWeb, testServerHandler);
        _channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = grpcWebHandler
        });
        _client = new Proto.FeatureService.FeatureServiceClient(_channel);

        // The shared WebAppFixture uses X-Honua-Test-Schema headers for schema-based isolation.
        // gRPC metadata entries become HTTP headers, so the server's schema middleware can read them.
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
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.FeatureService/QueryFeatures")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/QueryFeatures")]
    public async Task QueryFeatures_CountOnly_ReturnsCount()
    {
        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = WebAppFixture.TestServiceId,
            LayerId = WebAppFixture.TestLayerId,
            ReturnCountOnly = true
        };

        var response = await _client!.QueryFeaturesAsync(request, _headers);

        response.Should().NotBeNull();
        response.Count.Should().BeGreaterOrEqualTo(0);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.FeatureService/QueryFeaturesStream")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/QueryFeaturesStream")]
    public async Task QueryFeaturesStream_ReturnsPages()
    {
        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = WebAppFixture.TestServiceId,
            LayerId = WebAppFixture.TestLayerId,
            Where = "1=1"
        };

        var call = _client!.QueryFeaturesStream(request, _headers);
        var pages = new List<Proto.FeaturePage>();

        await foreach (var page in call.ResponseStream.ReadAllAsync())
        {
            pages.Add(page);
        }

        pages.Should().NotBeEmpty();
        pages.Last().IsLastPage.Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyEdits")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/ApplyEdits")]
    public async Task ApplyEdits_WithAdd_ReturnsResult()
    {
        var request = new Proto.ApplyEditsRequest
        {
            ServiceId = WebAppFixture.TestServiceId,
            LayerId = WebAppFixture.TestLayerId,
            RollbackOnFailure = true
        };
        request.Adds.Add(new Proto.Feature
        {
            Attributes = { ["Name"] = new Proto.AttributeValue { StringValue = "gRPC integration test" } },
            Geometry = new Proto.Geometry
            {
                Point = new Proto.PointGeometry { X = -157.85, Y = 21.30 }
            }
        });

        var response = await _client!.ApplyEditsAsync(request, _headers);

        response.Should().NotBeNull();
        response.AddResults.Should().ContainSingle();
        response.AddResults[0].Success.Should().BeTrue();
        response.AddResults[0].ObjectId.Should().BeGreaterThan(0);
    }
}
