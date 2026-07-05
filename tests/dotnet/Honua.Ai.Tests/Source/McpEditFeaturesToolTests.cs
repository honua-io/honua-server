// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.MapTools;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Focused coverage for the transactional feature-editing MCP tool
/// (<c>honua_edit_features</c>). The tool is a thin adapter over the shared
/// edit/transaction pipeline, so these tests substitute the canonical
/// <see cref="IEditProcessor"/> + <see cref="IFeatureWriter"/> and assert the
/// adapter builds the right protocol-neutral request, routes edits through the
/// single transactional batch apply, and projects the pipeline result back onto
/// the MCP per-edit + summary output.
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class McpEditFeaturesToolTests
{
    private const string ServiceId = "svc-parcels";
    private const string ServiceName = "Parcels";
    private const string ResourceId = "res-parcels";
    private const int LayerIndex = 0;
    private const int StorageLayerId = 42;

    private static readonly string MixedEditArguments = $$"""
        {
          "serviceId": "{{ServiceId}}",
          "layerId": {{LayerIndex}},
          "srid": 4326,
          "adds": [
            { "geometry": {"type":"Point","coordinates":[-97.74,30.27]}, "attributes": {"parcel_id":"A-1001"} },
            { "geometry": {"type":"Point","coordinates":[-97.75,30.28]}, "attributes": {"parcel_id":"A-1002"} }
          ],
          "updates": [ { "objectId": 42, "attributes": {"zoning":"R2"} } ],
          "deletes": [99]
        }
        """;

    private readonly IGeoprocessingJobService _jobService = Substitute.For<IGeoprocessingJobService>();

    [UnitTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /mcp tools/call honua_edit_features")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_EditFeatures_AppliesAddsUpdatesDeletes_InOneTransaction()
    {
        var editProcessor = Substitute.For<IEditProcessor>();
        UnifiedEditRequest captured = default;
        editProcessor.ValidateEdit(default, default!)
            .ReturnsForAnyArgs(EditValidationResult.Success());
        editProcessor.ToFeatureEditBatch(default, default!)
            .ReturnsForAnyArgs(ci =>
            {
                captured = ci.ArgAt<UnifiedEditRequest>(0);
                return FeatureEditBatch.Create();
            });

        var writer = Substitute.For<IFeatureWriter>();
        var capturedLayerId = -1;
        writer.ApplyEditsAsync(default, default, default)
            .ReturnsForAnyArgs(ci =>
            {
                capturedLayerId = ci.ArgAt<int>(0);
                return FeatureEditResult.Success(
                    createdCount: 2,
                    updatedCount: 1,
                    deletedCount: 1,
                    createdIds: [101L, 102L],
                    createResults: [EditOperationResult.Success(101), EditOperationResult.Success(102)],
                    updateResults: [EditOperationResult.Success(42)],
                    deleteResults: [EditOperationResult.Success(99)]);
            });

        var response = await DispatchEditAsync(editProcessor, writer, MixedEditArguments);

        response!.Error.Should().BeNull();
        var result = response.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeFalse();

        var structured = result.GetProperty("structuredContent");
        structured.GetProperty("addResults").GetArrayLength().Should().Be(2);
        structured.GetProperty("updateResults").GetArrayLength().Should().Be(1);
        structured.GetProperty("deleteResults").GetArrayLength().Should().Be(1);
        structured.GetProperty("addResults")[0].GetProperty("objectId").GetInt64().Should().Be(101);
        structured.GetProperty("addResults")[0].GetProperty("success").GetBoolean().Should().BeTrue();

        var summary = structured.GetProperty("summary");
        summary.GetProperty("applied").GetInt32().Should().Be(4);
        summary.GetProperty("failed").GetInt32().Should().Be(0);
        summary.GetProperty("rolledBack").GetBoolean().Should().BeFalse();

        // The adapter built the protocol-neutral request from the GeoJSON input:
        // 2 creates, 1 update, 1 delete, defaulting to all-or-nothing.
        captured.Creates!.Value.Length.Should().Be(2);
        captured.Updates!.Value.Length.Should().Be(1);
        captured.Deletes!.Value.Length.Should().Be(1);
        captured.TransactionOptions!.Value.RollbackOnFailure.Should().BeTrue(
            "rollbackOnFailure defaults to true (all-or-nothing)");

        // Edits ran through the single transactional batch apply against the
        // resolved storage layer, not per-feature writes.
        capturedLayerId.Should().Be(StorageLayerId);
        WriterCallNames(writer).Should().Equal("ApplyEditsAsync");
    }

    [UnitTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /mcp tools/call honua_edit_features")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_EditFeatures_RollbackOnFailure_ReportsRolledBack_AndLeavesStateUnchanged()
    {
        var editProcessor = Substitute.For<IEditProcessor>();
        UnifiedEditRequest captured = default;
        editProcessor.ValidateEdit(default, default!)
            .ReturnsForAnyArgs(EditValidationResult.Success());
        editProcessor.ToFeatureEditBatch(default, default!)
            .ReturnsForAnyArgs(ci =>
            {
                captured = ci.ArgAt<UnifiedEditRequest>(0);
                return FeatureEditBatch.Create(rollbackOnFailure: true);
            });

        var writer = Substitute.For<IFeatureWriter>();
        // A failing update rolls the whole transaction back: the shared result
        // reports WasRolledBack and no operation is committed.
        writer.ApplyEditsAsync(default, default, default)
            .ReturnsForAnyArgs(FeatureEditResult.Rollback(
                createResults: [EditOperationResult.Success(0), EditOperationResult.Success(0)],
                updateResults: [EditOperationResult.Failure("value out of range", 1000, 42)],
                deleteResults: [EditOperationResult.Success(99)]));

        var response = await DispatchEditAsync(editProcessor, writer, MixedEditArguments);

        response!.Error.Should().BeNull();
        var structured = response.Result!.Value.GetProperty("structuredContent");
        var summary = structured.GetProperty("summary");
        summary.GetProperty("rolledBack").GetBoolean().Should().BeTrue();
        summary.GetProperty("applied").GetInt32().Should().Be(0, "a rolled-back transaction commits nothing");
        summary.GetProperty("failed").GetInt32().Should().Be(4, "every submitted edit is reported failed on rollback");

        captured.TransactionOptions!.Value.RollbackOnFailure.Should().BeTrue();

        // State unchanged: only the transactional batch apply was attempted; there
        // were no independently-committing per-feature writes.
        WriterCallNames(writer).Should().Equal("ApplyEditsAsync");
    }

    [UnitTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /mcp tools/call honua_edit_features")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_EditFeatures_PerEditFailure_MapsErrorResult()
    {
        var editProcessor = Substitute.For<IEditProcessor>();
        editProcessor.ValidateEdit(default, default!)
            .ReturnsForAnyArgs(EditValidationResult.Success());
        editProcessor.ToFeatureEditBatch(default, default!)
            .ReturnsForAnyArgs(FeatureEditBatch.Create());

        var writer = Substitute.For<IFeatureWriter>();
        // rollbackOnFailure=false path: adds/delete commit, the one update fails.
        writer.ApplyEditsAsync(default, default, default)
            .ReturnsForAnyArgs(FeatureEditResult.Success(
                createdCount: 2,
                updatedCount: 0,
                deletedCount: 1,
                createdIds: [101L, 102L],
                createResults: [EditOperationResult.Success(101), EditOperationResult.Success(102)],
                updateResults: [EditOperationResult.Failure("field 'zoning' value too long", 1000, 42)],
                deleteResults: [EditOperationResult.Success(99)]));

        var arguments = $$"""
            {
              "serviceId": "{{ServiceId}}",
              "layerId": {{LayerIndex}},
              "updates": [ { "objectId": 42, "attributes": {"zoning":"RESIDENTIAL-VERY-LONG"} } ],
              "adds": [
                { "attributes": {"parcel_id":"A-1"} },
                { "attributes": {"parcel_id":"A-2"} }
              ],
              "deletes": [99],
              "rollbackOnFailure": false
            }
            """;

        var response = await DispatchEditAsync(editProcessor, writer, arguments);

        response!.Error.Should().BeNull();
        var structured = response.Result!.Value.GetProperty("structuredContent");
        var updateResult = structured.GetProperty("updateResults")[0];
        updateResult.GetProperty("success").GetBoolean().Should().BeFalse();
        updateResult.GetProperty("objectId").GetInt64().Should().Be(42);
        updateResult.GetProperty("error").GetString().Should().Contain("too long");

        var summary = structured.GetProperty("summary");
        summary.GetProperty("applied").GetInt32().Should().Be(3);
        summary.GetProperty("failed").GetInt32().Should().Be(1);
        summary.GetProperty("rolledBack").GetBoolean().Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /mcp tools/call honua_edit_features")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_EditFeatures_Unauthorized_ReturnsPermissionDeniedError()
    {
        _jobService
            .EnsureCallerAuthorizedAsync(default!, default, default, default)
            .ReturnsForAnyArgs(Task.FromException(new GeoprocessingAuthorizationException(requiresAuthentication: false)));

        var editProcessor = Substitute.For<IEditProcessor>();
        var writer = Substitute.For<IFeatureWriter>();

        var response = await DispatchEditAsync(editProcessor, writer, MixedEditArguments);

        response!.Error.Should().BeNull();
        var result = response.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        result.GetProperty("structuredContent").GetProperty("code").GetString().Should().Be("permission_denied");

        // Authorization is enforced before any edit reaches the writer.
        WriterCallNames(writer).Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /mcp tools/call honua_edit_features")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_EditFeatures_NoEdits_ReturnsInvalidArgument()
    {
        var editProcessor = Substitute.For<IEditProcessor>();
        var writer = Substitute.For<IFeatureWriter>();

        var arguments = $$"""
            { "serviceId": "{{ServiceId}}", "layerId": {{LayerIndex}} }
            """;

        var response = await DispatchEditAsync(editProcessor, writer, arguments);

        response!.Error.Should().BeNull();
        var result = response.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        result.GetProperty("structuredContent").GetProperty("code").GetString().Should().Be("invalid_argument");
    }

    [UnitTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /mcp tools/call honua_edit_features")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_EditFeatures_UpdateMissingObjectId_ReturnsInvalidArgument()
    {
        var editProcessor = Substitute.For<IEditProcessor>();
        var writer = Substitute.For<IFeatureWriter>();

        var arguments = $$"""
            {
              "serviceId": "{{ServiceId}}",
              "layerId": {{LayerIndex}},
              "updates": [ { "attributes": {"zoning":"R2"} } ]
            }
            """;

        var response = await DispatchEditAsync(editProcessor, writer, arguments);

        response!.Error.Should().BeNull();
        var result = response.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        result.GetProperty("structuredContent").GetProperty("code").GetString().Should().Be("invalid_argument");
    }

    private async Task<McpJsonRpcResponse?> DispatchEditAsync(
        IEditProcessor editProcessor,
        IFeatureWriter writer,
        string argumentsJson)
    {
        var surface = new McpOperatorSurface(
            [new EditFeaturesTool(_jobService, NullLogger<EditFeaturesTool>.Instance)],
            [],
            NullLogger<McpOperatorSurface>.Instance);

        var geometryService = Substitute.For<IGeometryService>();
        geometryService.ConvertGeoJsonToWkb(default, default).ReturnsForAnyArgs([0x01]);

        var services = new ServiceCollection();
        services.AddSingleton<IMetadataV2GraphProvider>(BuildGraphProvider());
        services.AddSingleton(editProcessor);
        services.AddSingleton(writer);
        services.AddSingleton(geometryService);
        var provider = services.BuildServiceProvider();

        var context = McpTestFactory.AuthenticatedHttpContext();
        context.RequestServices = provider;

        return await surface.DispatchAsync(
            context,
            ToolCall("edit-1", EditFeaturesTool.ToolName, argumentsJson),
            CancellationToken.None);
    }

    private static List<string> WriterCallNames(IFeatureWriter writer) =>
        writer.ReceivedCalls().Select(call => call.GetMethodInfo().Name).ToList();

    private static TestMetadataV2GraphProvider BuildGraphProvider()
    {
        var spatial = new MetadataV2ResourceSpatial
        {
            GeometryType = MetadataV2GeometryType.Point,
            SpatialReference = new MetadataV2SpatialReference { Srid = 4326 },
            Bbox = new MetadataV2Bbox { West = -180, South = -90, East = 180, North = 90 }
        };

        return new TestMetadataV2GraphBuilder()
            .AddResource(ResourceId, "Parcels Dataset", spatial: spatial)
            .AddStorageBinding("bind-parcels", ResourceId, "public.parcels", storageLayerId: StorageLayerId)
            .AddService(ServiceId, ServiceName)
            .AddPublication("pub-parcels", ServiceId, ResourceId, layerIndex: LayerIndex, storageBindingId: "bind-parcels")
            .BuildProvider();
    }

    private static McpJsonRpcRequest ToolCall(string id, string toolName, string argumentsJson) => new()
    {
        JsonRpc = "2.0",
        Id = JsonString(id),
        Method = "tools/call",
        Params = Json($$"""
            {"name":"{{toolName}}","arguments":{{argumentsJson}}}
            """)
    };

    private static JsonElement JsonString(string value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return document.RootElement.Clone();
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
