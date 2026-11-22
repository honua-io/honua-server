// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

public sealed class ServiceApplyEditsResponseTests
{
    [UnitTest]
    public async Task Issue5490_ServiceApplyEditsResponse_IsTheBarePerLayerArray()
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = new MemoryStream();
        var result = FeatureServerEndpoints.CreateServiceApplyEditsResult(
        [
            new ServiceLayerEditResult
            {
                Id = 0,
                AddResults = [new EditResult { ObjectId = 302271, Success = true }],
                UpdateResults = [],
                DeleteResults = []
            }
        ]);

        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);

        document.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        var layerResult = document.RootElement.EnumerateArray().Should().ContainSingle().Subject;
        layerResult.GetProperty("id").GetInt32().Should().Be(0);
        layerResult.GetProperty("addResults")[0].GetProperty("objectId").GetInt64().Should().Be(302271);
        layerResult.GetProperty("updateResults").GetArrayLength().Should().Be(0);
        layerResult.GetProperty("deleteResults").GetArrayLength().Should().Be(0);
    }

    [UnitTest]
    public void ServiceApplyEditsLayerFailure_WithHttpOrEditCode_MapsSpecificEsriEditErrorCodes()
    {
        FeatureServerEndpoints.ResolveLayerFailureErrorCode(StatusCodes.Status403Forbidden, objectId: 10, isAdd: false, isDelete: false)
            .Should().Be(GeoServicesEditErrorCodes.NotPermitted);
        FeatureServerEndpoints.ResolveLayerFailureErrorCode(StatusCodes.Status404NotFound, objectId: 10, isAdd: false, isDelete: false)
            .Should().Be(GeoServicesEditErrorCodes.NotFound);
        FeatureServerEndpoints.ResolveLayerFailureErrorCode(StatusCodes.Status404NotFound, objectId: 10, isAdd: false, isDelete: true)
            .Should().Be(GeoServicesEditErrorCodes.DeleteNotFound);
        FeatureServerEndpoints.ResolveLayerFailureErrorCode(StatusCodes.Status409Conflict, objectId: 10, isAdd: false, isDelete: false)
            .Should().Be(GeoServicesEditErrorCodes.UpdateConflict);
        FeatureServerEndpoints.ResolveLayerFailureErrorCode(StatusCodes.Status400BadRequest, objectId: 10, isAdd: false, isDelete: false)
            .Should().Be(GeoServicesEditErrorCodes.ValidationFailed);
        FeatureServerEndpoints.ResolveLayerFailureErrorCode(StatusCodes.Status400BadRequest, objectId: null, isAdd: false, isDelete: false)
            .Should().Be(GeoServicesEditErrorCodes.InvalidObjectId);
        FeatureServerEndpoints.ResolveLayerFailureErrorCode(GeoServicesEditErrorCodes.ValidationFailed, objectId: null, isAdd: true, isDelete: false)
            .Should().Be(GeoServicesEditErrorCodes.ValidationFailed);

        var deleteFailures = FeatureServerEndpoints.BuildDeleteFailureResults([42L, -1L], "Layer failed", StatusCodes.Status404NotFound);
        deleteFailures.Should().NotBeNull().And.HaveCount(2);
        deleteFailures![0].Error!.Code.Should().Be(GeoServicesEditErrorCodes.DeleteNotFound);
        deleteFailures[1].Error!.Code.Should().Be(GeoServicesEditErrorCodes.InvalidObjectId);
    }

    [UnitTest]
    public void BuildTopFeaturesJsonResponse_WithOutFieldsAndGeometryOptions_ProjectsFieldsAndFormatsGeometry()
    {
        var resource = new MetadataV2Object
        {
            SchemaFields =
            [
                new MetadataV2Field { Name = "OBJECTID", Type = MetadataV2FieldType.BigInteger, SemanticRoles = ["id.primary"] },
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String },
                new MetadataV2Field { Name = "pop", Type = MetadataV2FieldType.Integer }
            ],
            Spatial = new MetadataV2ObjectSpatial
            {
                GeometryType = MetadataV2GeometryType.Point,
                SpatialReference = new MetadataV2SpatialReference { Srid = 4326 }
            }
        };

        var wkbPointZ = new byte[1 + 4 + 8 + 8 + 8];
        wkbPointZ[0] = 1;
        BitConverter.GetBytes(1001u).CopyTo(wkbPointZ, 1);
        BitConverter.GetBytes(1.123456).CopyTo(wkbPointZ, 5);
        BitConverter.GetBytes(2.654321).CopyTo(wkbPointZ, 13);
        BitConverter.GetBytes(99.5).CopyTo(wkbPointZ, 21);

        var feature = Feature.Create(1, wkbPointZ, new Dictionary<string, object?>
        {
            ["OBJECTID"] = 1L,
            ["name"] = "Honolulu",
            ["pop"] = 350000
        }.ToImmutableDictionary());
        var queryResult = QueryResult<Feature>.Create(1, [feature]);

        var limits = new GeometryLimits { MaxCoordinatePrecision = 2 };
        var response = FeatureServerEndpoints.BuildTopFeaturesJsonResponse(
            queryResult,
            resource,
            returnGeometry: true,
            outputSrid: 3857,
            geometryLimits: limits,
            returnZ: true,
            returnM: false,
            outFields: ["name"]);

        response.SpatialReference.Should().NotBeNull();
        response.SpatialReference!.Wkid.Should().Be(3857);
        response.Fields.Should().ContainSingle(f => f.Name == "name");
        var outFeature = response.Features.Should().ContainSingle().Subject;
        outFeature.Attributes.Should().ContainKey("name").WhoseValue.Should().Be("Honolulu");
        outFeature.Attributes.Should().NotContainKey("pop");
        outFeature.Geometry.Should().NotBeNull();
        outFeature.Geometry!.X.Should().Be(1.12);
        outFeature.Geometry.Y.Should().Be(2.65);
        outFeature.Geometry.Z.Should().Be(99.5);
    }

    [UnitTest]
    public void BuildBinFieldsAndDateBinFields_PopulatesFields()
    {
        var resource = new MetadataV2Object
        {
            SchemaFields =
            [
                new MetadataV2Field { Name = "magnitude", Type = MetadataV2FieldType.Float },
                new MetadataV2Field { Name = "region", Type = MetadataV2FieldType.String }
            ]
        };

        var fixedBinFields = FeatureServerEndpoints.BuildBinFields(resource, new BinDefinition
        {
            Type = BinType.FixedInterval,
            Field = "magnitude",
            IntervalSize = 1.0
        });
        fixedBinFields.Select(f => f.Name).Should().Equal("lowerBoundary", "upperBoundary", "count");

        var dateBinFields = FeatureServerEndpoints.BuildDateBinFields(resource,
        [
            new StatisticDefinition
            {
                StatisticType = StatisticType.Avg,
                OnStatisticField = "magnitude",
                OutStatisticFieldName = "avg_mag"
            }
        ]);
        dateBinFields.Select(f => f.Name).Should().Equal("boundary", "avg_mag");
        dateBinFields[0].Type.Should().Be("esriFieldTypeDate");
        dateBinFields[1].Type.Should().Be("esriFieldTypeDouble");

    }

    [UnitTest]
    public void TryParseDateBin_WithFixedSeconds_PreservesInterval()
    {
        FeatureServerEndpoints.TryParseDateBin(
            """{"fixedBin":{"intervalCount":250,"intervalUnit":"seconds"}}""",
            "observed_at", out var dateBin, out var error).Should().BeTrue();
        error.Should().BeNull();
        dateBin.FixedInterval.Should().Be(TimeSpan.FromSeconds(250));
    }

    [UnitTheory]
    [InlineData("milliseconds")]
    [InlineData("months")]
    [InlineData("years")]
    [InlineData("fortnights")]
    public void TryParseDateBin_WithUnsupportedFixedUnit_RejectsInterval(string unit)
    {
        var json = $$$"""{"fixedBin":{"intervalCount":1,"intervalUnit":"{{{unit}}}"}}""";
        FeatureServerEndpoints.TryParseDateBin(json, "observed_at", out _, out var error)
            .Should().BeFalse();
        error.Should().Contain("fixedBin.intervalUnit");
    }

    [UnitTest]
    public void AttachmentAndReplicaModels_WithErrorAndSyncModel_SerializeCleanly()
    {
        var addResultWithError = new AddAttachmentResponse
        {
            AddAttachmentResult = new AddAttachmentResult
            {
                ObjectId = 0,
                Success = false,
                Error = new EditError { Code = GeoServicesEditErrorCodes.GenericFailure, Description = "Failed to add attachment." }
            }
        };
        var addJson = JsonSerializer.SerializeToElement(addResultWithError, FeatureServerJsonContext.Default.AddAttachmentResponse);
        addJson.GetProperty("addAttachmentResult").GetProperty("error").GetProperty("code").GetInt32().Should().Be(1000);

        var perLayerReplica = new CreateReplicaResponse
        {
            ReplicaId = "rep-1",
            ReplicaName = "test",
            SyncModel = "perLayer",
            ServerGen = null,
            LayerServerGens = [new ReplicaInfoLayerServerGeneration { Id = 0, ServerGen = 12, ServerSibGen = 12 }]
        };
        var perLayerJson = JsonSerializer.SerializeToElement(perLayerReplica, FeatureServerJsonContext.Default.CreateReplicaResponse);
        perLayerJson.TryGetProperty("serverGen", out _).Should().BeFalse();
        perLayerJson.GetProperty("layerServerGens").GetArrayLength().Should().Be(1);
    }
}
