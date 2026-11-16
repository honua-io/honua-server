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
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

// End-to-end global-ID and typed-value behaviour against PostGIS: layer 0 declares a UUID
// global-ID field for these tests; layer 1 declares none.
[Collection("Database")]
[Protocol(TestProtocols.Grpc)]
public sealed class GrpcGlobalIdIntegrationTests : IAsyncLifetime
{
    private const string GlobalIdField = "globalid";
    private const string AddedName = "gRPC global-ID test";

    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Community);
    private GrpcChannel? _channel;
    private Proto.FeatureService.FeatureServiceClient? _client;
    private Metadata? _headers;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        await _fixture.UpdateV2ResourceAsync(WebAppFixture.TestLayerId, resource => resource with
        {
            Editing = (resource.Editing ?? new MetadataV2ObjectEditing()) with { GlobalIdField = GlobalIdField },
            SchemaFields = [.. resource.SchemaFields, new MetadataV2Field { Name = GlobalIdField, Type = MetadataV2FieldType.Uuid, Nullable = true }],
        });
        await ProvisionGlobalIdIndexAsync(GlobalIdField);
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
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyEdits")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/ApplyEdits")]
    public async Task MissingGlobalIdIndex_DisablesCapabilitiesAndRefusesEditsBeforeWriting()
    {
        var request = new Proto.DescribeLayerRequest { ServiceId = WebAppFixture.TestServiceId, LayerId = WebAppFixture.TestLayerId };
        (await _client!.DescribeLayerAsync(request, _headers)).Layer.Capabilities.Create.Should().BeTrue();
        var count = await _fixture.CountStoredFeaturesAsync(WebAppFixture.TestLayerId);
        await using (var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema!))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                DO $test$ BEGIN
                    EXECUTE format('DROP INDEX %I', 'idx_features_gid_' || md5('0:globalid'));
                END $test$;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var capabilities = (await _client.DescribeLayerAsync(request, _headers)).Layer.Capabilities;
        capabilities.Create.Should().BeFalse();
        capabilities.Update.Should().BeFalse();
        capabilities.Delete.Should().BeFalse();
        capabilities.Query.Should().BeTrue();
        var edit = new Proto.ApplyEditsRequest { ServiceId = WebAppFixture.TestServiceId, LayerId = WebAppFixture.TestLayerId };
        edit.Adds.Add(new Proto.Feature { Attributes = { ["name"] = new Proto.AttributeValue { StringValue = AddedName } } });
        var action = async () => await _client.ApplyEditsAsync(edit, _headers);
        (await action.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);
        (await _fixture.CountStoredFeaturesAsync(WebAppFixture.TestLayerId)).Should().Be(count);
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyEdits")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/ApplyEdits")]
    public async Task GlobalIds_AddQueryUpdateDelete_RoundTripThroughPostgis()
    {
        var globalId = await AddAsync();

        var selected = await QueryAsync(globalId, outFields: "name");
        selected.GlobalIdFieldName.Should().Be(GlobalIdField);
        var feature = selected.Features.Should().ContainSingle().Subject;
        feature.GlobalId.Should().Be(globalId);
        feature.Attributes.Should().NotContainKey(GlobalIdField);

        var update = new Proto.ApplyEditsRequest { ServiceId = WebAppFixture.TestServiceId, LayerId = WebAppFixture.TestLayerId, UseGlobalIds = true };
        update.Updates.Add(new Proto.Feature { GlobalId = globalId.ToUpperInvariant(), Attributes = { ["name"] = new Proto.AttributeValue { StringValue = "renamed" } } });
        var updated = await _client!.ApplyEditsAsync(update, _headers);
        updated.UpdateResults.Should().ContainSingle().Which.Should().Match<Proto.EditResult>(r => r.Success && r.GlobalId == globalId);
        (await _fixture.ReadStoredFeatureNameAsync(WebAppFixture.TestLayerId, updated.UpdateResults[0].ObjectId)).Should().Be("renamed");

        var delete = new Proto.ApplyEditsRequest { ServiceId = WebAppFixture.TestServiceId, LayerId = WebAppFixture.TestLayerId, UseGlobalIds = true };
        delete.DeleteGlobalIds.Add(globalId);
        (await _client.ApplyEditsAsync(delete, _headers)).DeleteResults.Should().ContainSingle().Which.GlobalId.Should().Be(globalId);
        (await QueryAsync(globalId, outFields: "*")).Features.Should().BeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyEdits")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/ApplyEdits")]
    public async Task ObjectIdEdits_ReportTheStoredGlobalId()
    {
        const string customGlobalIdField = "native_identity";
        await _fixture.UpdateV2ResourceAsync(WebAppFixture.TestLayerId, resource => resource with
        {
            Editing = resource.Editing! with { GlobalIdField = customGlobalIdField },
            SchemaFields = [.. resource.SchemaFields, new MetadataV2Field { Name = customGlobalIdField, Type = MetadataV2FieldType.Uuid, Nullable = true }]
        });
        await ProvisionGlobalIdIndexAsync(customGlobalIdField);
        var globalId = await AddAsync();
        var objectId = (await QueryAsync(globalId, "name")).Features.Should().ContainSingle().Subject.Id;
        var update = new Proto.ApplyEditsRequest { ServiceId = WebAppFixture.TestServiceId, LayerId = WebAppFixture.TestLayerId };
        update.Updates.Add(new Proto.Feature { Id = objectId, Attributes = { ["name"] = new Proto.AttributeValue { StringValue = "object ID update" } } });

        var updated = await _client!.ApplyEditsAsync(update, _headers);

        updated.UpdateResults.Should().ContainSingle().Which.Should().Match<Proto.EditResult>(result => result.Success && result.GlobalId == globalId);
        var delete = new Proto.ApplyEditsRequest { ServiceId = WebAppFixture.TestServiceId, LayerId = WebAppFixture.TestLayerId };
        delete.Deletes.Add(objectId);
        var deleted = await _client.ApplyEditsAsync(delete, _headers);
        deleted.DeleteResults.Should().ContainSingle().Which.Should().Match<Proto.EditResult>(result => result.Success && result.GlobalId == globalId);
        (await QueryAsync(globalId, "name")).Features.Should().BeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyEdits")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/ApplyEdits")]
    public async Task GlobalIdDelete_MissingTarget_FailsWholeCallWithoutWrites()
    {
        var before = await _fixture.CountStoredFeaturesAsync(WebAppFixture.TestLayerId);
        var delete = new Proto.ApplyEditsRequest { ServiceId = WebAppFixture.TestServiceId, LayerId = WebAppFixture.TestLayerId, UseGlobalIds = true };
        delete.DeleteGlobalIds.Add(Guid.NewGuid().ToString("D"));

        var act = async () => await _client!.ApplyEditsAsync(delete, _headers);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.NotFound);
        (await _fixture.CountStoredFeaturesAsync(WebAppFixture.TestLayerId)).Should().Be(before);
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyEdits")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/ApplyEdits")]
    public async Task DateValue_IsStoredAndReadBackAsCanonicalText()
    {
        var globalId = await AddAsync(new Proto.AttributeValue { DateValue = "2026-11-14" });

        var feature = (await QueryAsync(globalId, outFields: "created_date")).Features.Should().ContainSingle().Subject;

        feature.Attributes["created_date"].StringValue.Should().StartWith("2026-11-14");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.FeatureService/QueryFeatures")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/QueryFeatures")]
    public async Task GlobalIdQuery_OnLayerWithoutGlobalIdField_FailsPrecondition()
    {
        var request = new Proto.QueryFeaturesRequest { ServiceId = WebAppFixture.TestServiceId, LayerId = 1 };
        request.GlobalIds.Add(Guid.NewGuid().ToString("D"));

        var act = async () => await _client!.QueryFeaturesAsync(request, _headers);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.FeatureService/QueryFeaturesStream")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/QueryFeaturesStream")]
    public async Task GlobalIdStream_ReportsIdentityWithProjectedAttributes()
    {
        var globalId = await AddAsync();
        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = WebAppFixture.TestServiceId,
            LayerId = WebAppFixture.TestLayerId,
            ReturnGeometry = false,
            OutFields = { "name" },
            GlobalIds = { globalId }
        };
        using var stream = _client!.QueryFeaturesStream(request, _headers);

        (await stream.ResponseStream.MoveNext(CancellationToken.None)).Should().BeTrue();
        var page = stream.ResponseStream.Current;
        page.GlobalIdFieldName.Should().Be(GlobalIdField);
        var feature = page.Features.Should().ContainSingle().Subject;
        feature.GlobalId.Should().Be(globalId);
        feature.Attributes.Should().ContainKey("name").And.NotContainKey(GlobalIdField);
        (await stream.ResponseStream.MoveNext(CancellationToken.None)).Should().BeFalse();
    }

    private async Task ProvisionGlobalIdIndexAsync(string field)
    {
        // The fixture mutates an in-memory graph, bypassing the production metadata
        // store that provisions this index when a GlobalID declaration is activated.
        await using var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema!);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT format('CREATE UNIQUE INDEX %I ON features (((NULLIF(attributes ->> %L, %L))::uuid)) WHERE layer_id = %s',
                'idx_features_gid_' || md5(@layer::text || ':' || @field), @field, '', @layer)
            """;
        command.Parameters.AddWithValue("layer", WebAppFixture.TestLayerId);
        command.Parameters.AddWithValue("field", field);
        command.CommandText = (string)(await command.ExecuteScalarAsync())!;
        command.Parameters.Clear();
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string> AddAsync(Proto.AttributeValue? createdDate = null)
    {
        var add = new Proto.Feature
        {
            Attributes = { ["name"] = new Proto.AttributeValue { StringValue = AddedName } },
            Geometry = new Proto.Geometry { Point = new Proto.PointGeometry { X = -157.85, Y = 21.30 } }
        };
        if (createdDate is not null)
        {
            add.Attributes["created_date"] = createdDate;
        }

        var request = new Proto.ApplyEditsRequest { ServiceId = WebAppFixture.TestServiceId, LayerId = WebAppFixture.TestLayerId };
        request.Adds.Add(add);
        var response = await _client!.ApplyEditsAsync(request, _headers);
        var result = response.AddResults.Should().ContainSingle().Subject;
        result.Success.Should().BeTrue();
        Guid.TryParseExact(result.GlobalId, "D", out _).Should().BeTrue("the server assigns a global ID to every add");
        return result.GlobalId;
    }

    private async Task<Proto.QueryFeaturesResponse> QueryAsync(string globalId, string outFields)
    {
        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = WebAppFixture.TestServiceId,
            LayerId = WebAppFixture.TestLayerId,
            OutFields = { outFields },
            ReturnGeometry = false
        };
        request.GlobalIds.Add(globalId);
        return await _client!.QueryFeaturesAsync(request, _headers);
    }
}
