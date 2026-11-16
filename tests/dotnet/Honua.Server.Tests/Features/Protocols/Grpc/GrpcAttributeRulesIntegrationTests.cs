// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

[Collection("Database")]
[Protocol(TestProtocols.Grpc)]
public sealed class GrpcAttributeRulesIntegrationTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Community);
    private GrpcChannel? _channel;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        await _fixture.UpdateV2ResourceAsync(WebAppFixture.TestLayerId, resource => resource with
        {
            AttributeRules =
            [
                new MetadataV2AttributeRule
                {
                    Name = "canonical-name", Type = MetadataV2AttributeRuleType.Calculation,
                    FieldName = "name", ScriptExpression = "return 'calculated';", IsEnabled = true,
                },
            ],
        });
        _channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = new GrpcWebHandler(GrpcWebMode.GrpcWeb, _fixture.CreateHandler()),
        });
    }

    public async Task DisposeAsync()
    {
        _channel?.Dispose();
        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyEdits")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/ApplyEdits")]
    public async Task ApplyEdits_CalculationRule_PersistsCalculatedValue()
    {
        var headers = new Metadata();
        if (_fixture.CurrentSchema is { } schema)
        {
            headers.Add("X-Honua-Test-Schema", schema);
        }

        var client = new Proto.FeatureService.FeatureServiceClient(_channel!);
        var feature = new Proto.Feature
        {
            Geometry = new Proto.Geometry { Point = new Proto.PointGeometry { X = -157.85, Y = 21.30 } },
        };
        feature.Attributes["name"] = new Proto.AttributeValue { StringValue = "submitted" };
        var edits = new Proto.ApplyEditsRequest { ServiceId = WebAppFixture.TestServiceId, LayerId = WebAppFixture.TestLayerId };
        edits.Adds.Add(feature);
        var added = (await client.ApplyEditsAsync(edits, headers)).AddResults.Should().ContainSingle().Subject;
        added.Success.Should().BeTrue();
        var query = new Proto.QueryFeaturesRequest
        {
            ServiceId = WebAppFixture.TestServiceId, LayerId = WebAppFixture.TestLayerId,
            ObjectIds = { added.ObjectId }, OutFields = { "name" }, ReturnGeometry = false,
        };
        var stored = (await client.QueryFeaturesAsync(query, headers)).Features.Should().ContainSingle().Subject;
        stored.Attributes["name"].StringValue.Should().Be("calculated");
    }
}
