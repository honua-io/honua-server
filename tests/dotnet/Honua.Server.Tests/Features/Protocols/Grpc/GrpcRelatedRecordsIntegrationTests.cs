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
[Operation(Operations.Query)]
public sealed partial class GrpcRelatedRecordsIntegrationTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Community)
        .OverrideConfiguration(new Dictionary<string, string?>
        {
            ["Limits:Query:MaxRecordCount"] = "100",
            ["Limits:Query:DefaultRecordCount"] = "100"
        });
    private GrpcChannel? _channel;
    private Proto.FeatureService.FeatureServiceClient? _client;
    private Metadata? _headers;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        await _fixture.UpdateV2ResourceAsync(WebAppFixture.TestLayerId, resource => resource with
        {
            Relationships = [new MetadataV2Relationship
            {
                Id = "77", Name = "same-category", RelatedResourceId = resource.Metadata.Id,
                Role = "origin", Cardinality = "many-to-many", OriginField = "category", DestinationField = "category"
            }]
        });
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

    [IntegrationTheory]
    [InlineData(101, 1)]
    [InlineData(1, 101)]
    [Endpoint("POST /geospatial.v1.FeatureService/QueryRelatedRecords")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/QueryRelatedRecords")]
    public async Task RelatedRecords_AboveConfiguredQueryLimit_RejectsRequest(int originCount, int pageSize)
    {
        var request = new Proto.QueryRelatedRecordsRequest
        {
            ServiceId = WebAppFixture.TestServiceId, LayerId = WebAppFixture.TestLayerId,
            RelationshipId = 77, PageSize = pageSize
        };
        request.ObjectIds.AddRange(Enumerable.Range(1, originCount).Select(id => (long)id));

        var call = async () => await _client!.QueryRelatedRecordsAsync(request, _headers);

        var exception = await call.Should().ThrowAsync<RpcException>();
        exception.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [IntegrationTest]
    [Endpoint("POST /geospatial.v1.FeatureService/QueryRelatedRecords")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/QueryRelatedRecords")]
    public async Task RelatedRecords_GrpcWebPages_ReturnEveryMatchingPairOnce()
    {
        var query = new Proto.QueryFeaturesRequest
        {
            ServiceId = WebAppFixture.TestServiceId, LayerId = WebAppFixture.TestLayerId,
            OutFields = { "category" }, ReturnGeometry = true, OutSr = new Proto.SpatialReference { Wkid = 3857 }
        };
        var seeded = (await _client!.QueryFeaturesAsync(query, _headers)).Features;
        seeded.Should().HaveCountGreaterThan(1);
        var expected = seeded.SelectMany(origin => seeded
            .Where(child => child.Attributes["category"].StringValue == origin.Attributes["category"].StringValue)
            .Select(child => (origin.Id, child.Id))).ToArray();
        expected.Length.Should().BeGreaterThan(seeded.Count);
        var request = new Proto.QueryRelatedRecordsRequest
        {
            ServiceId = WebAppFixture.TestServiceId, LayerId = WebAppFixture.TestLayerId,
            RelationshipId = 77, PageSize = 1, ReturnGeometry = true, OutFields = { "category" },
            OutSr = new Proto.SpatialReference { Wkid = 3857 }
        };
        request.ObjectIds.AddRange(seeded.Select(feature => feature.Id));
        var actual = new List<(long Origin, long Child)>();
        var pages = 0;
        do
        {
            (++pages).Should().BeLessThanOrEqualTo(expected.Length + 1, "paging must make progress");
            var page = await _client.QueryRelatedRecordsAsync(request, _headers);
            page.RelatedLayerId.Should().Be(WebAppFixture.TestLayerId);
            page.SpatialReference.Wkid.Should().Be(3857);
            foreach (var child in page.Groups.SelectMany(group => group.Features))
            {
                child.Geometry.Should().Be(seeded.Single(feature => feature.Id == child.Id).Geometry);
            }
            page.Groups.Sum(group => Math.Max(1, group.Features.Count)).Should().BeLessThanOrEqualTo(1);
            actual.AddRange(page.Groups.SelectMany(group => group.Features.Select(child => (group.ObjectId, child.Id))));
            request.PageToken = page.NextPageToken;
            actual.Count.Should().BeLessThanOrEqualTo(expected.Length, "paging must terminate without repeating pairs");
        } while (request.PageToken.Length > 0);

        actual.Should().OnlyHaveUniqueItems().And.BeEquivalentTo(expected);
    }

    [IntegrationTest]
    [Endpoint("POST /geospatial.v1.FeatureService/QueryRelatedRecords")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/QueryRelatedRecords")]
    public async Task RelatedRecords_NarrowProjection_PreservesChildGlobalId()
    {
        await ConfigureGlobalIdAsync();
        var add = new Proto.ApplyEditsRequest
        {
            ServiceId = WebAppFixture.TestServiceId, LayerId = WebAppFixture.TestLayerId,
            Adds = { new Proto.Feature
            {
                Attributes =
                {
                    ["name"] = new Proto.AttributeValue { StringValue = "related-global-id" },
                    ["category"] = new Proto.AttributeValue { StringValue = Guid.NewGuid().ToString("D") }
                },
                Geometry = new Proto.Geometry { Point = new Proto.PointGeometry { X = -157.85, Y = 21.30 } }
            } }
        };
        var added = (await _client!.ApplyEditsAsync(add, _headers)).AddResults.Should().ContainSingle().Subject;
        added.Success.Should().BeTrue();
        Guid.TryParseExact(added.GlobalId, "D", out _).Should().BeTrue();
        var request = new Proto.QueryRelatedRecordsRequest
        {
            ServiceId = WebAppFixture.TestServiceId, LayerId = WebAppFixture.TestLayerId,
            RelationshipId = 77, ObjectIds = { added.ObjectId }, OutFields = { "NAME" }, ReturnGeometry = false
        };

        var response = await _client.QueryRelatedRecordsAsync(request, _headers);

        response.GlobalIdFieldName.Should().Be("globalid");
        var child = response.Groups.Should().ContainSingle().Subject.Features.Should().ContainSingle().Subject;
        child.GlobalId.Should().Be(added.GlobalId);
        child.Attributes.Should().ContainKey("name").And.NotContainKey("globalid").And.NotContainKey("category");
    }

    [IntegrationTest]
    [Endpoint("POST /geospatial.v1.FeatureService/QueryRelatedRecords")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/QueryRelatedRecords")]
    public async Task RelatedRecords_FilterWithNoMatches_PreservesEveryRequestedOrigin()
    {
        var request = new Proto.QueryRelatedRecordsRequest
        {
            ServiceId = WebAppFixture.TestServiceId, LayerId = WebAppFixture.TestLayerId,
            RelationshipId = 77, PageSize = 10, Where = "name = 'absent-related-row'", ReturnGeometry = false
        };
        request.ObjectIds.AddRange([1L, 2L]);

        var response = await _client!.QueryRelatedRecordsAsync(request, _headers);

        response.Groups.Select(group => group.ObjectId).Should().Equal(1L, 2L);
        response.Groups.Should().OnlyContain(group => group.Features.Count == 0);
        response.NextPageToken.Should().BeEmpty();
    }
    private async Task ConfigureGlobalIdAsync()
    {
        await _fixture.UpdateV2ResourceAsync(WebAppFixture.TestLayerId, resource => resource with
        {
            Editing = (resource.Editing ?? new MetadataV2ResourceEditing()) with { GlobalIdField = "globalid" },
            SchemaFields = [.. resource.SchemaFields, new MetadataV2Field
            {
                Name = "globalid", Type = MetadataV2FieldType.Uuid, Nullable = true
            }]
        });
    }

}
