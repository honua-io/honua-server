// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using FluentAssertions.Execution;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

public sealed class GrpcHiddenFieldTestsFixture : IAsyncLifetime
{
    public WebAppFixture App { get; } = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);

    public async Task InitializeAsync()
    {
        await App.InitializeAsync();
        App.UpdateV2ResourceSchemaField(0, new MetadataV2Field
        {
            Name = "category",
            Type = MetadataV2FieldType.String,
            Hidden = true
        });
    }

    public Task DisposeAsync() => App.DisposeAsync();

}

[Collection("Database")]
[Protocol(TestProtocols.Grpc)]
[Operation(Operations.Query)]
public sealed class GrpcHiddenFieldTests : IClassFixture<GrpcHiddenFieldTestsFixture>
{
    private readonly WebAppFixture _fixture;

    public GrpcHiddenFieldTests(GrpcHiddenFieldTestsFixture fixture)
    {
        _fixture = fixture.App;
    }

    [IntegrationTheory]
    [InlineData(false, null)]
    [InlineData(false, "*")]
    [InlineData(false, "name,category")]
    [InlineData(true, null)]
    [InlineData(true, "*")]
    [InlineData(true, "name,category")]
    [Endpoint("POST /geospatial.v1.FeatureService/QueryFeatures")]
    [Endpoint("POST /geospatial.v1.FeatureService/QueryFeaturesStream")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/QueryFeatures")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/QueryFeaturesStream")]
    public async Task Output_OmitsHiddenFields(bool streaming, string? outFields)
    {
        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = new GrpcWebHandler(GrpcWebMode.GrpcWeb, _fixture.CreateHandler())
        });
        var client = new Proto.FeatureService.FeatureServiceClient(channel);
        var headers = new Metadata { { "X-Honua-Test-Schema", _fixture.CurrentSchema! } };
        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = WebAppFixture.TestServiceId,
            LayerId = 0
        };
        if (outFields != null)
        {
            request.OutFields.AddRange(outFields.Split(','));
        }

        var fields = new List<Proto.FieldDefinition>();
        var features = new List<Proto.Feature>();
        if (streaming)
        {
            using var call = client.QueryFeaturesStream(request, headers);
            await foreach (var page in call.ResponseStream.ReadAllAsync())
            {
                fields.AddRange(page.Fields);
                features.AddRange(page.Features);
            }
        }
        else
        {
            var response = await client.QueryFeaturesAsync(request, headers);
            fields.AddRange(response.Fields);
            features.AddRange(response.Features);
        }

        using var scope = new AssertionScope();
        fields.Select(f => f.Name).Should().Contain("name").And.NotContain("category");
        features.Should().HaveCount(5);
        features.Single(f => f.Id == 1).Attributes["name"].StringValue.Should().Be("Test Feature");
        features.Should().OnlyContain(f => !f.Attributes.ContainsKey("category"));
    }
}
