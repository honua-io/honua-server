// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
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

// Server-maintained editor-tracking fields through the real writer and reader: forged client
// values never persist, an update keeps the stored creator values, and the stamped calendar date
// and timestamp read back through QueryFeatures.
[Collection("Database")]
[Protocol(TestProtocols.Grpc)]
public sealed class GrpcEditorTrackingIntegrationTests : IAsyncLifetime
{
    private const string Forged = "forged-user";

    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Community);
    private GrpcChannel? _channel;
    private Proto.FeatureService.FeatureServiceClient? _client;
    private Metadata? _headers;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        // The seeded created_date field is a calendar date; last_edited_date is a timestamp.
        await _fixture.UpdateV2ResourceAsync(WebAppFixture.TestLayerId, resource => resource with
        {
            Editing = (resource.Editing ?? new MetadataV2ObjectEditing()) with
            {
                CreatorField = "created_user",
                CreatedAtField = "created_date",
                EditorField = "last_edited_user",
                UpdatedAtField = "last_edited_date",
            },
            SchemaFields =
            [
                .. resource.SchemaFields,
                new MetadataV2Field { Name = "created_user", Type = MetadataV2FieldType.String, Nullable = true },
                new MetadataV2Field { Name = "last_edited_user", Type = MetadataV2FieldType.String, Nullable = true },
                new MetadataV2Field { Name = "last_edited_date", Type = MetadataV2FieldType.DateTime, Nullable = true },
            ],
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

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyEdits")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/ApplyEdits")]
    public async Task ApplyEdits_ForgedTrackingValues_AreStampedAndKeptAcrossUpdate()
    {
        var before = DateTimeOffset.UtcNow;
        var add = ForgedFeature(id: 0, name: "tracked");
        add.Geometry = new Proto.Geometry { Point = new Proto.PointGeometry { X = -157.85, Y = 21.30 } };
        var addRequest = new Proto.ApplyEditsRequest { ServiceId = WebAppFixture.TestServiceId, LayerId = WebAppFixture.TestLayerId };
        addRequest.Adds.Add(add);
        var added = (await _client!.ApplyEditsAsync(addRequest, _headers)).AddResults.Should().ContainSingle().Subject;
        added.Success.Should().BeTrue();

        var inserted = await ReadAsync(added.ObjectId);
        Text(inserted, "created_user").Should().NotBe(Forged).And.Be(Text(inserted, "last_edited_user"));
        DateOnly.ParseExact(Text(inserted, "created_date")![..10], "yyyy-MM-dd", CultureInfo.InvariantCulture)
            .Should().BeOnOrAfter(DateOnly.FromDateTime(before.UtcDateTime));
        var insertedAt = inserted.Attributes["last_edited_date"].DatetimeValue;
        insertedAt.Should().BeGreaterThanOrEqualTo(before.ToUnixTimeMilliseconds());

        var updateRequest = new Proto.ApplyEditsRequest { ServiceId = WebAppFixture.TestServiceId, LayerId = WebAppFixture.TestLayerId };
        updateRequest.Updates.Add(ForgedFeature(added.ObjectId, name: "renamed"));
        (await _client.ApplyEditsAsync(updateRequest, _headers)).UpdateResults.Should().ContainSingle().Which.Success.Should().BeTrue();

        var updated = await ReadAsync(added.ObjectId);
        Text(updated, "name").Should().Be("renamed");
        Text(updated, "created_user").Should().Be(Text(inserted, "created_user"));
        Text(updated, "created_date").Should().Be(Text(inserted, "created_date"));
        Text(updated, "last_edited_user").Should().NotBe(Forged);
        updated.Attributes["last_edited_date"].DatetimeValue.Should().BeGreaterThanOrEqualTo(insertedAt);
    }

    private static Proto.Feature ForgedFeature(long id, string name)
    {
        var feature = new Proto.Feature { Id = id };
        feature.Attributes["name"] = new Proto.AttributeValue { StringValue = name };
        feature.Attributes["created_user"] = new Proto.AttributeValue { StringValue = Forged };
        feature.Attributes["last_edited_user"] = new Proto.AttributeValue { StringValue = Forged };
        feature.Attributes["created_date"] = new Proto.AttributeValue { DateValue = "2001-01-01" };
        feature.Attributes["last_edited_date"] = new Proto.AttributeValue { DatetimeValue = 0 };
        return feature;
    }

    private async Task<Proto.Feature> ReadAsync(long objectId)
    {
        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = WebAppFixture.TestServiceId,
            LayerId = WebAppFixture.TestLayerId,
            Where = $"objectid = {objectId.ToString(CultureInfo.InvariantCulture)}",
            OutFields = { "*" },
            ReturnGeometry = false
        };
        return (await _client!.QueryFeaturesAsync(request, _headers)).Features.Should().ContainSingle().Subject;
    }

    // Null when the attribute is absent or null, so anonymous fixture callers compare cleanly.
    private static string? Text(Proto.Feature feature, string name)
        => feature.Attributes.TryGetValue(name, out var value) && value.ValueCase == Proto.AttributeValue.ValueOneofCase.StringValue
            ? value.StringValue
            : null;
}
