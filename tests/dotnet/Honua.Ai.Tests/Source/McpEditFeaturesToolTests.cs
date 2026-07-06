// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Geoprocessing;
using Honua.Infrastructure.Authentication;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.MapTools;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Focused coverage for the transactional feature-editing MCP tool
/// (<c>honua_edit_features</c>). The tool is a thin adapter over the shared
/// edit/transaction pipeline, so these tests substitute the canonical
/// <see cref="IEditProcessor"/> + <see cref="IFeatureWriter"/> and assert the
/// adapter builds the right protocol-neutral request, routes edits through the
/// single transactional batch apply, and projects the pipeline result back onto
/// the MCP per-edit + summary output. Per-layer edit RBAC runs through the REAL
/// shared seams (<see cref="AccessPolicyEvaluator"/>,
/// <c>ServiceDataEditorAuthorization</c>, <see cref="IPermissionResolver"/>
/// grants), not lookalikes, so the authorization tests exercise the same
/// primitives the HTTP edit surfaces enforce.
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
    public async Task ToolsCall_EditFeatures_PrincipalWithoutEditGrants_IsPermissionDenied_ForEachEditType()
    {
        // An authenticated caller with NO edit roles/grants (query-only) must be
        // denied per edit type by the shared per-layer RBAC gate — generic
        // process-execution rights alone cannot mutate a layer.
        var editProcessor = Substitute.For<IEditProcessor>();
        var writer = Substitute.For<IFeatureWriter>();
        var queryOnly = AuthenticatedPrincipal();

        var cases = new (string Arguments, string ExpectedOperation)[]
        {
            ($$"""{"serviceId":"{{ServiceId}}","layerId":{{LayerIndex}},"adds":[ {"attributes":{"parcel_id":"A-1"} } ] }""", "Insert"),
            ($$"""{"serviceId":"{{ServiceId}}","layerId":{{LayerIndex}},"updates":[ {"objectId":42,"attributes":{"zoning":"R2"} } ] }""", "Update"),
            ($$"""{"serviceId":"{{ServiceId}}","layerId":{{LayerIndex}},"deletes":[99] }""", "Delete"),
        };

        foreach (var (arguments, expectedOperation) in cases)
        {
            var response = await DispatchEditAsync(editProcessor, writer, arguments, principal: queryOnly);

            response!.Error.Should().BeNull();
            var result = response.Result!.Value;
            result.GetProperty("isError").GetBoolean().Should().BeTrue(
                $"a caller without edit grants must be denied '{expectedOperation}'");
            var structured = result.GetProperty("structuredContent");
            structured.GetProperty("code").GetString().Should().Be("permission_denied");
            structured.GetProperty("message").GetString().Should().Contain(expectedOperation,
                "the structured denial must name the missing permission");
        }

        // No edit ever reached the pipeline.
        WriterCallNames(writer).Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /mcp tools/call honua_edit_features")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_EditFeatures_InsertOnlyGrant_AllowsAdds_ButRejectsMixedAddUpdateUpFront()
    {
        // A per-operation Insert grant (canonical IPermissionResolver, #1376)
        // authorizes an adds-only call, but a mixed add+update call is rejected
        // WHOLE and UP FRONT because the caller lacks the Update grant — no
        // partial application.
        var resolver = Substitute.For<IPermissionResolver>();
        resolver.AuthorizeAsync(default!, default!, default!, default, default, default, default)
            .ReturnsForAnyArgs(ci => ci.ArgAt<AuthorizationOperation>(4) == AuthorizationOperation.Insert
                ? PermissionDecision.Allow(new PermissionGrant { Service = ServiceName, Layer = "*", Operation = "insert" })
                : PermissionDecision.NoMatch());

        var fieldCrew = AuthenticatedPrincipal("field-crew");

        var editProcessor = Substitute.For<IEditProcessor>();
        editProcessor.ValidateEdit(default, default!)
            .ReturnsForAnyArgs(EditValidationResult.Success());
        editProcessor.ToFeatureEditBatch(default, default!)
            .ReturnsForAnyArgs(FeatureEditBatch.Create());

        var writer = Substitute.For<IFeatureWriter>();
        writer.ApplyEditsAsync(default, default, default)
            .ReturnsForAnyArgs(FeatureEditResult.Success(
                createdCount: 1,
                updatedCount: 0,
                deletedCount: 0,
                createdIds: [101L],
                createResults: [EditOperationResult.Success(101)]));

        // adds-only: the Insert grant authorizes the call.
        var addsOnly = $$"""{"serviceId":"{{ServiceId}}","layerId":{{LayerIndex}},"adds":[ {"attributes":{"parcel_id":"A-1"} } ] }""";
        var allowed = await DispatchEditAsync(editProcessor, writer, addsOnly, principal: fieldCrew, permissionResolver: resolver);

        allowed!.Error.Should().BeNull();
        allowed.Result!.Value.GetProperty("isError").GetBoolean().Should().BeFalse(
            "an Insert grant must authorize an adds-only call");
        WriterCallNames(writer).Should().Equal("ApplyEditsAsync");

        writer.ClearReceivedCalls();

        // add + update: missing Update grant rejects the whole request up front.
        var mixed = $$"""
            {"serviceId":"{{ServiceId}}","layerId":{{LayerIndex}},
             "adds":[ {"attributes":{"parcel_id":"A-2"} } ],
             "updates":[ {"objectId":42,"attributes":{"zoning":"R2"} } ] }
            """;
        var denied = await DispatchEditAsync(editProcessor, writer, mixed, principal: fieldCrew, permissionResolver: resolver);

        denied!.Error.Should().BeNull();
        var result = denied.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        var structured = result.GetProperty("structuredContent");
        structured.GetProperty("code").GetString().Should().Be("permission_denied");
        structured.GetProperty("message").GetString().Should().Contain("Update",
            "the structured denial must name the missing grant");

        // Nothing was applied — not even the authorized adds.
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
        string argumentsJson,
        ClaimsPrincipal? principal = null,
        IPermissionResolver? permissionResolver = null)
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

        // The REAL shared authorization seams the HTTP edit surfaces run: the
        // access-policy evaluator plus the RBAC options that drive the
        // data-editor role gate ("data-editor" is a global data-editor role in
        // these tests). A per-operation permission resolver is registered only
        // when a test exercises grant-based authorization.
        services.AddSingleton<IAccessPolicyEvaluator>(new AccessPolicyEvaluator());
        services.AddSingleton(Options.Create(new RbacOptions { DataEditorRoles = ["data-editor"] }));
        if (permissionResolver is not null)
        {
            services.AddSingleton(permissionResolver);
        }

        var provider = services.BuildServiceProvider();

        var context = new DefaultHttpContext
        {
            RequestServices = provider,
            User = principal ?? DataEditorPrincipal()
        };

        return await surface.DispatchAsync(
            context,
            ToolCall("edit-1", EditFeaturesTool.ToolName, argumentsJson),
            CancellationToken.None);
    }

    /// <summary>Authenticated principal holding the global data-editor role.</summary>
    private static ClaimsPrincipal DataEditorPrincipal() => AuthenticatedPrincipal("data-editor");

    /// <summary>Authenticated principal with the supplied role claims.</summary>
    private static ClaimsPrincipal AuthenticatedPrincipal(params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, "test-user") };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
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
