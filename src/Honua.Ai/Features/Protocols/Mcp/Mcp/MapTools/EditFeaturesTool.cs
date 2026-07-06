// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Geoprocessing;
using Honua.Infrastructure.Authentication;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using AccessDecision = Honua.Core.Features.Security.Domain.AccessDecision;

namespace Honua.Ai.Protocols.Mcp.MapTools;

/// <summary>
/// MCP tool that transactionally applies feature edits (adds, updates, deletes)
/// to a single published editable layer. It is a thin adapter over the shared
/// edit/transaction pipeline the FeatureServer <c>applyEdits</c>, OGC API
/// Features, WFS Transaction, and OData CRUD surfaces all use: it resolves the
/// layer through the same Metadata v2 snapshot, converts GeoJSON geometry to WKB
/// via the shared <see cref="IGeometryService"/>, builds a protocol-neutral
/// <see cref="UnifiedEditRequest"/>, runs it through the canonical
/// <see cref="IEditProcessor"/> (validation + <c>ToFeatureEditBatch</c>), and
/// executes through <see cref="IFeatureWriter.ApplyEditsAsync(int, FeatureEditBatch, System.Threading.CancellationToken)"/>.
/// No edit, validation, transaction, or optimistic-concurrency semantics are
/// reimplemented here — the writer's own transaction is the all-or-nothing
/// boundary when <c>rollbackOnFailure</c> is set. Per-layer edit RBAC is
/// enforced through the same shared authorization seams as the HTTP edit
/// surfaces (<see cref="AccessPolicyHelpers.EvaluateResourceAccessAsync"/> and
/// <see cref="ServiceDataEditorAuthorization.EvaluateResourceDataEditorAsync"/>),
/// per edit type: Insert for adds, Update for updates, Delete for deletes.
/// </summary>
internal sealed class EditFeaturesTool : IMcpTool
{
    /// <summary>The advertised <c>tools/list</c> name of this tool.</summary>
    public const string ToolName = "honua_edit_features";

    private const string ToolDescription =
        "Transactionally edit features on a published layer (by serviceId/layerId): apply adds, updates, and deletes in a single call. "
        + "When rollbackOnFailure=true (the default) the edits are all-or-nothing — any failed edit rolls back the entire transaction and leaves the layer unchanged; "
        + "when false, successful edits commit independently and per-edit failures are reported. "
        + "Geometry is supplied as RFC 7946 GeoJSON geometry objects and attributes as a flat name/value map; the input CRS defaults to EPSG:4326 (override with 'srid'). "
        + "updates must carry an 'objectId'; deletes are a list of object IDs; adds receive store-assigned object IDs. "
        + "Discover the layer first with honua_resolve_entity or honua_list_layers to obtain serviceId/layerId, and verify the attribute/geometry schema by reading a feature with honua_query_features before editing. "
        + "The caller must hold per-layer edit permission for every edit type in the call (Insert for adds, Update for updates, Delete for deletes); a missing permission rejects the whole request with permission_denied before any edit is applied. "
        + "Returns per-edit results (index, success, objectId, error) plus a transaction summary (applied, failed, rolledBack).";

    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger<EditFeaturesTool> _logger;

    public EditFeaturesTool(IGeoprocessingJobService jobService, ILogger<EditFeaturesTool> logger)
    {
        _jobService = jobService;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Execution;

    /// <inheritdoc />
    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Edit features",
        Description = ToolDescription,
        InputSchema = MapToolSchemas.EditFeaturesArgumentSchema,
        OutputSchema = McpToolOutputSchemas.EditFeaturesOutputSchema,
        // Write tool: deletes destroy existing state, so destructiveHint is true;
        // a replay applies the same adds again (new rows) and is therefore not
        // idempotent.
        Annotations = McpToolAnnotationSets.Write("Edit features", destructive: true, idempotent: false)
    };

    /// <inheritdoc />
    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("EditFeatures");
        McpLog.ToolInvoked(_logger, ToolName, WorkflowFamily);

        // Standard MCP write authorization: the same operator-grant gate every
        // MCP write tool (execute_plan, etc.) enforces. Per-layer edit RBAC is
        // enforced separately below, once the target layer is resolved.
        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        await _jobService
            .EnsureCallerAuthorizedAsync(principal, OperatorResourceType.Process, OperatorOperation.Execute, cancellationToken)
            .ConfigureAwait(false);

        var argument = McpToolHelpers.ParseArguments(arguments, MapToolJsonContext.Default.McpEditFeaturesArgument);

        var graphProvider = httpContext.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var layer = MapToolLayerResolver.Resolve(snapshot, argument.ServiceId, argument.LayerId);

        var srid = argument.Srid ?? 4326;
        if (srid <= 0)
        {
            throw new GeoprocessingValidationException("'srid' must be a positive SRID/WKID.");
        }

        var hasAdds = argument.Adds is { Count: > 0 };
        var hasUpdates = argument.Updates is { Count: > 0 };
        var hasDeletes = argument.Deletes is { Count: > 0 };
        if (!hasAdds && !hasUpdates && !hasDeletes)
        {
            throw new GeoprocessingValidationException(
                "At least one of 'adds', 'updates', or 'deletes' must be provided.");
        }

        // Per-layer edit RBAC through the same shared seams the FeatureServer
        // single-verb edit endpoints run (BH3-001/BH3-014): the operation-aware
        // resource-access seam (tenant scope, per-operation grants, AccessPolicy)
        // plus the data-editor write gate (layer-scoped write keys, explicit write
        // policy, per-operation RBAC grants, admin/data-editor roles). Every edit
        // type present in the request is authorized UP FRONT — a caller missing any
        // required permission gets a structured permission_denied naming the missing
        // grant and no edit is applied.
        await EnsureLayerEditAuthorizedAsync(httpContext, layer, hasAdds, hasUpdates, hasDeletes, cancellationToken)
            .ConfigureAwait(false);

        var rollbackOnFailure = argument.RollbackOnFailure ?? true;
        var returnEditResults = argument.ReturnEditResults ?? true;

        var geometryService = httpContext.RequestServices.GetRequiredService<IGeometryService>();

        var editRequest = new UnifiedEditRequest
        {
            Creates = hasAdds ? BuildCreates(argument.Adds!, geometryService, srid) : null,
            Updates = hasUpdates ? BuildUpdates(argument.Updates!, geometryService, srid) : null,
            Deletes = hasDeletes ? BuildDeletes(argument.Deletes!) : null,
            TransactionOptions = new EditTransactionOptions
            {
                RollbackOnFailure = rollbackOnFailure,
                UseExplicitTransaction = rollbackOnFailure
            },
            ValidationOptions = EditValidationOptions.Strict()
        };

        var editProcessor = httpContext.RequestServices.GetRequiredService<IEditProcessor>();
        var validation = editProcessor.ValidateEdit(editRequest, layer.Resource);
        if (!validation.IsValid)
        {
            throw new GeoprocessingValidationException(
                validation.ErrorMessage ?? "The edit request failed validation.");
        }

        var editBatch = editProcessor.ToFeatureEditBatch(editRequest, layer.Resource);

        var featureWriter = httpContext.RequestServices.GetRequiredService<IFeatureWriter>();
        var result = await featureWriter
            .ApplyEditsAsync(layer.StorageLayerId, editBatch, cancellationToken)
            .ConfigureAwait(false);

        var output = BuildOutput(layer.Service.Metadata.Id, argument.LayerId!.Value, result, returnEditResults);
        return McpToolHelpers.SuccessResult(output, MapToolJsonContext.Default.McpEditFeaturesOutput);
    }

    /// <summary>
    /// Authorizes every edit type present in the request against the resolved
    /// layer via the shared per-operation authorization seams: adds require
    /// <see cref="AuthorizationOperation.Insert"/>, updates
    /// <see cref="AuthorizationOperation.Update"/>, and deletes
    /// <see cref="AuthorizationOperation.Delete"/>. Checks run before any edit is
    /// built or applied, so a missing grant rejects the whole request up front
    /// rather than partially applying it.
    /// </summary>
    private static async Task EnsureLayerEditAuthorizedAsync(
        HttpContext httpContext,
        MapToolLayerContext layer,
        bool hasAdds,
        bool hasUpdates,
        bool hasDeletes,
        CancellationToken cancellationToken)
    {
        if (hasAdds)
        {
            await RequireLayerOperationAsync(httpContext, layer, AuthorizationOperation.Insert, "adds", cancellationToken)
                .ConfigureAwait(false);
        }

        if (hasUpdates)
        {
            await RequireLayerOperationAsync(httpContext, layer, AuthorizationOperation.Update, "updates", cancellationToken)
                .ConfigureAwait(false);
        }

        if (hasDeletes)
        {
            await RequireLayerOperationAsync(httpContext, layer, AuthorizationOperation.Delete, "deletes", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task RequireLayerOperationAsync(
        HttpContext httpContext,
        MapToolLayerContext layer,
        AuthorizationOperation operation,
        string editKind,
        CancellationToken cancellationToken)
    {
        // 1) Operation-aware resource access (tenant scope, per-operation grants,
        //    coarse AccessPolicy) — the same seam the FeatureServer edit endpoints
        //    run pre-body via AccessPolicyHelpers.RequireResourceAccessAsync.
        var decision = await AccessPolicyHelpers.EvaluateResourceAccessAsync(
            httpContext,
            layer.Resource,
            layer.Service,
            operation,
            cancellationToken).ConfigureAwait(false);

        // 2) The per-edit-type data-editor write gate (layer-scoped write keys,
        //    explicit write policy, per-operation RBAC grants, admin/data-editor
        //    roles) — the decision-shaped core of
        //    ServiceDataEditorAuthorization.RequireResourceDataEditorAsync.
        if (decision.IsAllowed)
        {
            decision = await ServiceDataEditorAuthorization.EvaluateResourceDataEditorAsync(
                httpContext,
                layer.Resource,
                layer.Service,
                operation,
                cancellationToken).ConfigureAwait(false);
        }

        ThrowIfDenied(decision, layer, operation, editKind);
    }

    private static void ThrowIfDenied(
        in AccessDecision decision,
        in MapToolLayerContext layer,
        AuthorizationOperation operation,
        string editKind)
    {
        if (decision.IsAllowed)
        {
            return;
        }

        if (decision.RequiresAuthentication)
        {
            throw new GeoprocessingAuthorizationException(requiresAuthentication: true);
        }

        throw new GeoprocessingAuthorizationException(
            requiresAuthentication: false,
            $"Caller lacks the '{operation}' permission required to apply '{editKind}' on layer "
            + $"'{layer.Resource.Metadata.Name}' of service '{layer.Service.Metadata.Name}'.");
    }

    private static ImmutableArray<EditFeature> BuildCreates(
        IReadOnlyList<McpEditFeature> features,
        IGeometryService geometryService,
        int srid)
    {
        var builder = ImmutableArray.CreateBuilder<EditFeature>(features.Count);
        for (var i = 0; i < features.Count; i++)
        {
            var feature = features[i]
                ?? throw new GeoprocessingValidationException($"'adds[{i}]' must be a feature object.");
            var geometry = ConvertGeometry(feature.Geometry, geometryService, srid, $"adds[{i}]");
            builder.Add(EditFeature.ForCreate(geometry, ToAttributes(feature.Attributes, $"adds[{i}]"), feature.GlobalId));
        }

        return builder.MoveToImmutable();
    }

    private static ImmutableArray<EditFeature> BuildUpdates(
        IReadOnlyList<McpEditFeature> features,
        IGeometryService geometryService,
        int srid)
    {
        var builder = ImmutableArray.CreateBuilder<EditFeature>(features.Count);
        for (var i = 0; i < features.Count; i++)
        {
            var feature = features[i]
                ?? throw new GeoprocessingValidationException($"'updates[{i}]' must be a feature object.");
            if (feature.ObjectId is not { } objectId)
            {
                // The shared Updates set is keyed on objectId; global-id-only updates
                // would require useGlobalIds resolution, which the shared edit pipeline
                // does not support on this path (GeoServices applyEdits rejects it too).
                throw new GeoprocessingValidationException(
                    $"'updates[{i}]' must carry an 'objectId' identifying the feature to update.");
            }

            var geometry = ConvertGeometry(feature.Geometry, geometryService, srid, $"updates[{i}]");
            builder.Add(EditFeature.ForUpdate(objectId, geometry, ToAttributes(feature.Attributes, $"updates[{i}]")));
        }

        return builder.MoveToImmutable();
    }

    private static ImmutableArray<long> BuildDeletes(IReadOnlyList<long> deletes)
    {
        var builder = ImmutableArray.CreateBuilder<long>(deletes.Count);
        builder.AddRange(deletes);
        return builder.MoveToImmutable();
    }

    private static byte[]? ConvertGeometry(JsonNode? geometry, IGeometryService geometryService, int srid, string path)
    {
        if (geometry is null)
        {
            return null;
        }

        if (geometry is not JsonObject)
        {
            throw new GeoprocessingValidationException($"'{path}.geometry' must be a GeoJSON geometry object.");
        }

        return geometryService.ConvertGeoJsonToWkb(geometry.ToJsonString(), srid)
            ?? throw new GeoprocessingValidationException(
                $"'{path}.geometry' could not be converted from GeoJSON to a geometry.");
    }

    private static ImmutableDictionary<string, object?> ToAttributes(JsonNode? attributes, string path)
    {
        if (attributes is null)
        {
            return ImmutableDictionary<string, object?>.Empty;
        }

        if (attributes is not JsonObject obj)
        {
            throw new GeoprocessingValidationException($"'{path}.attributes' must be a JSON object.");
        }

        var builder = ImmutableDictionary.CreateBuilder<string, object?>();
        foreach (var pair in obj)
        {
            builder[pair.Key] = ToClrValue(pair.Value);
        }

        return builder.ToImmutable();
    }

    private static object? ToClrValue(JsonNode? value)
    {
        if (value is not JsonValue jsonValue)
        {
            // Nested objects/arrays are round-tripped as their JSON text so the
            // shared validator/writer can coerce them per the target field schema.
            return value?.ToJsonString();
        }

        if (jsonValue.TryGetValue<bool>(out var boolean))
        {
            return boolean;
        }

        if (jsonValue.TryGetValue<long>(out var integer))
        {
            return integer;
        }

        if (jsonValue.TryGetValue<double>(out var number))
        {
            return number;
        }

        if (jsonValue.TryGetValue<string>(out var text))
        {
            return text;
        }

        return jsonValue.ToJsonString();
    }

    private static McpEditFeaturesOutput BuildOutput(
        string serviceId,
        int layerId,
        FeatureEditResult result,
        bool returnEditResults)
    {
        var failed =
            CountFailures(result.CreateResults) +
            CountFailures(result.UpdateResults) +
            CountFailures(result.DeleteResults);

        return new McpEditFeaturesOutput
        {
            ServiceId = serviceId,
            LayerId = layerId,
            AddResults = returnEditResults ? MapResults(result.CreateResults) : [],
            UpdateResults = returnEditResults ? MapResults(result.UpdateResults) : [],
            DeleteResults = returnEditResults ? MapResults(result.DeleteResults) : [],
            Summary = new McpEditSummary
            {
                Applied = result.CreatedCount + result.UpdatedCount + result.DeletedCount,
                Failed = failed,
                RolledBack = result.WasRolledBack
            }
        };
    }

    private static McpEditResult[] MapResults(ImmutableArray<EditOperationResult> results)
    {
        if (results.IsDefaultOrEmpty)
        {
            return [];
        }

        var mapped = new McpEditResult[results.Length];
        for (var i = 0; i < results.Length; i++)
        {
            var op = results[i];
            mapped[i] = new McpEditResult
            {
                Index = i,
                Success = op.IsSuccess,
                ObjectId = op.ObjectId,
                GlobalId = op.GlobalId,
                Error = op.IsSuccess ? null : op.ErrorMessage
            };
        }

        return mapped;
    }

    private static int CountFailures(ImmutableArray<EditOperationResult> results)
    {
        if (results.IsDefaultOrEmpty)
        {
            return 0;
        }

        var count = 0;
        foreach (var op in results)
        {
            if (!op.IsSuccess)
            {
                count++;
            }
        }

        return count;
    }
}
