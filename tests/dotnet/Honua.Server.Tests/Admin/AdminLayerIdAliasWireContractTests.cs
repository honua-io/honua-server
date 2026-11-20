// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Admin.Share;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Admin;

// Admin replies that renamed layerId to storageLayerId or serviceLayerIndex keep the old name as a
// deprecated alias, so published clients that still read layerId keep working.
public sealed class AdminLayerIdAliasWireContractTests
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    public static TheoryData<string, object, string, string> Replies() => new()
    {
        { "LayerRelationshipResponse", new LayerRelationshipResponse { StorageLayerId = 41 }, "storageLayerId", "layerId" },
        { "LayerRelationshipItem", RelationshipItem(), "relatedStorageLayerId", "relatedLayerId" },
        { "LayerAuthoringDocumentResponse", new LayerAuthoringDocumentResponse { StorageLayerId = 41 }, "storageLayerId", "layerId" },
        { "LayerFieldConfigurationResponse", new LayerFieldConfigurationResponse { StorageLayerId = 41 }, "storageLayerId", "layerId" },
        { "LayerFilterConfigurationResponse", new LayerFilterConfigurationResponse { StorageLayerId = 41 }, "storageLayerId", "layerId" },
        { "LayerValidationResponse", new LayerValidationResponse { StorageLayerId = 41, LayerName = "Roads", Status = "valid" }, "storageLayerId", "layerId" },
        { "LayerMetadataResponse", new LayerMetadataResponse { ServiceLayerIndex = 41, LayerName = "Roads" }, "serviceLayerIndex", "layerId" },
        { "ShareExportDefinitionResponse", ExportDefinition(), "serviceLayerIndex", "layerId" },
        { "ShareItemRefResponse", new ShareItemRefResponse { ServiceName = "roads", ServiceLayerIndex = 41 }, "serviceLayerIndex", "layerId" },
    };

    [UnitTheory]
    [MemberData(nameof(Replies))]
    public void Reply_KeepsTheDeprecatedLayerIdAlias(string schemaName, object reply, string property, string alias)
    {
        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(reply, reply.GetType(), Wire));
        Assert.Equal(41, wire.RootElement.GetProperty(property).GetInt32());
        Assert.Equal(41, wire.RootElement.GetProperty(alias).GetInt32());

        using var spec = JsonDocument.Parse(File.ReadAllText(RepositoryPaths.Resolve("docs", "developer", "api-specs", "admin-api.json")));
        var properties = spec.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty(schemaName).GetProperty("properties");
        Assert.True(properties.TryGetProperty(property, out _));
        Assert.True(properties.GetProperty(alias).GetProperty("deprecated").GetBoolean());
    }

    private static LayerRelationshipItem RelationshipItem() => new()
    {
        Id = "rel",
        Name = "Inspections",
        RelatedStorageLayerId = 41,
        Role = "origin",
        Cardinality = "OneToMany",
        OriginField = "id",
        DestinationField = "asset_id",
    };

    private static ShareExportDefinitionResponse ExportDefinition() => new()
    {
        ExportId = "export",
        ServiceName = "roads",
        ServiceLayerIndex = 41,
        DestinationType = "s3",
        DestinationStatus = "ready",
        DestinationConfig = [],
        Format = "geojson",
        Schedule = "daily",
        ScheduleState = "active",
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };
}
