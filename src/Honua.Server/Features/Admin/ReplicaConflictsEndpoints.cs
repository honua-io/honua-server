// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Licensing;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Protocols.GeoServices.FeatureServer;
using Honua.Server.Features.Protocols.GeoServices.FeatureServer.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Operator/Console admin API for named disconnected replicas and manual sync conflict review (#1167).
/// </summary>
internal static partial class ReplicaConflictsEndpoints
{
    private const string ConflictReviewEntitlement = "replica.conflict-review";
    private const string ConflictReviewFeatureName = "Disconnected Replica Conflict Review";
    private const int MaxReplicaPageSize = 200;
    private const int MaxConflictPageSize = 200;
    private const int DefaultPageSize = 50;

    public static IEndpointRouteBuilder MapReplicaConflictsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var replicas = endpoints.MapGroup("/api/v{version:apiVersion}/admin/replicas")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Replicas")
            .WithDescription("Named disconnected replica metadata and sync conflict review.")
            .RequireAdminAuthorization();

        _ = replicas.MapGet("", HandleListReplicasAsync)
            .WithName("ListAdminReplicas")
            .WithSummary("List active disconnected replicas across services.");

        _ = replicas.MapGet("/{replicaId}", HandleGetReplicaAsync)
            .WithName("GetAdminReplica")
            .WithSummary("Get full metadata for a single named replica.");

        _ = replicas.MapGet("/{replicaId}/conflicts", HandleListConflictsAsync)
            .WithName("ListReplicaConflicts")
            .WithSummary("List durable sync conflicts for a replica.");

        _ = replicas.MapGet("/{replicaId}/conflicts/{conflictId:guid}", HandleGetConflictAsync)
            .WithName("GetReplicaConflict")
            .WithSummary("Get a conflict with base/client/server feature states.");

        _ = replicas.MapPost("/{replicaId}/conflicts/{conflictId:guid}/resolve", HandleResolveConflictAsync)
            .WithName("ResolveReplicaConflict")
            .WithSummary("Apply a resolution to a pending sync conflict.");

        return endpoints;
    }

    private static async Task<IResult> HandleListReplicasAsync(
        [FromQuery] string? serviceId,
        [FromQuery] string? status,
        [FromQuery] int? limit,
        [FromQuery] string? afterReplicaId,
        [FromServices] IReplicaRepository repository,
        [FromServices] IReplicaConflictStore conflictStore,
        HttpContext context)
    {
        var entitlementError = RequireEntitlement(context);
        if (entitlementError is not null)
        {
            return entitlementError;
        }

        var pageSize = ClampPageSize(limit, MaxReplicaPageSize);
        var records = await repository.ListAllAsync(
            NormalizeFilter(serviceId), NormalizeFilter(status), pageSize, NormalizeFilter(afterReplicaId), context.RequestAborted)
            .ConfigureAwait(false);

        var replicaIds = records.Select(r => r.ReplicaId).ToArray();
        var pending = await conflictStore.CountPendingByReplicaAsync(replicaIds, context.RequestAborted).ConfigureAwait(false);

        var summaries = records
            .Select(record => new ReplicaAdminSummary
            {
                ReplicaId = record.ReplicaId,
                ReplicaName = record.ReplicaName,
                ServiceId = record.ServiceId,
                Owner = record.Owner,
                DeviceClient = record.DeviceClient,
                SyncModel = record.SyncModel,
                SyncDirection = record.SyncDirection,
                Status = record.Status,
                CreatedAt = record.CreatedAt,
                LastSyncTime = record.LastSyncTime,
                LastSyncGeneration = record.LastSyncGeneration,
                PendingConflicts = pending.TryGetValue(record.ReplicaId, out var count) ? count : 0,
            })
            .ToArray();

        return Results.Json(summaries, ReplicaConflictsJsonContext.Default.ReplicaAdminSummaryArray);
    }

    private static async Task<IResult> HandleGetReplicaAsync(
        string replicaId,
        [FromServices] IReplicaRepository repository,
        [FromServices] IReplicaConflictStore conflictStore,
        HttpContext context)
    {
        var entitlementError = RequireEntitlement(context);
        if (entitlementError is not null)
        {
            return entitlementError;
        }

        var record = await repository.GetAsync(replicaId, context.RequestAborted).ConfigureAwait(false);
        if (record is null)
        {
            return NotFound(context, $"Replica '{replicaId}' was not found.");
        }

        var value = record.Value;
        var editAccessError = await RequireReplicaEditAccessAsync(context, value.ServiceId).ConfigureAwait(false);
        if (editAccessError is not null)
        {
            return editAccessError;
        }

        var pending = await conflictStore.CountPendingByReplicaAsync([replicaId], context.RequestAborted).ConfigureAwait(false);

        var detail = new ReplicaAdminDetail
        {
            ReplicaId = value.ReplicaId,
            ReplicaName = value.ReplicaName,
            ServiceId = value.ServiceId,
            Owner = value.Owner,
            DeviceClient = value.DeviceClient,
            SyncModel = value.SyncModel,
            SyncDirection = value.SyncDirection,
            Status = value.Status,
            LayerIds = value.LayerIds,
            ReplicaGeometryJson = value.ReplicaGeometryJson,
            BranchVersionId = value.BranchVersionId,
            CreatedAt = value.CreatedAt,
            LastSyncTime = value.LastSyncTime,
            LastSyncGeneration = value.LastSyncGeneration,
            PendingConflicts = pending.TryGetValue(replicaId, out var count) ? count : 0,
        };

        return Results.Json(detail, ReplicaConflictsJsonContext.Default.ReplicaAdminDetail);
    }

    private static async Task<IResult> HandleListConflictsAsync(
        string replicaId,
        [FromQuery] bool? pending,
        [FromQuery] int? limit,
        [FromQuery] Guid? afterId,
        [FromServices] IReplicaRepository repository,
        [FromServices] IReplicaConflictStore conflictStore,
        HttpContext context)
    {
        var entitlementError = RequireEntitlement(context);
        if (entitlementError is not null)
        {
            return entitlementError;
        }

        var record = await repository.GetAsync(replicaId, context.RequestAborted).ConfigureAwait(false);
        if (record is null)
        {
            return NotFound(context, $"Replica '{replicaId}' was not found.");
        }

        var editAccessError = await RequireReplicaEditAccessAsync(context, record.Value.ServiceId).ConfigureAwait(false);
        if (editAccessError is not null)
        {
            return editAccessError;
        }

        var pageSize = ClampPageSize(limit, MaxConflictPageSize);
        var conflicts = await conflictStore.ListByReplicaAsync(
            replicaId, pending ?? true, pageSize, afterId, context.RequestAborted).ConfigureAwait(false);

        var summaries = conflicts
            .Select(conflict => new ConflictSummary
            {
                ConflictId = conflict.ConflictId.ToString(),
                SyncOpId = conflict.SyncOpId.ToString(),
                LayerId = conflict.LayerId,
                ObjectId = conflict.ObjectId,
                ConflictType = ConflictTypeToString(conflict.ConflictType),
                Resolution = conflict.Resolution is { } resolution ? ResolutionToString(resolution) : null,
                BaseGeneration = conflict.BaseGeneration,
                CreatedAt = conflict.CreatedAt,
            })
            .ToArray();

        return Results.Json(summaries, ReplicaConflictsJsonContext.Default.ConflictSummaryArray);
    }

    private static async Task<IResult> HandleGetConflictAsync(
        string replicaId,
        Guid conflictId,
        [FromServices] IReplicaConflictStore conflictStore,
        HttpContext context)
    {
        var entitlementError = RequireEntitlement(context);
        if (entitlementError is not null)
        {
            return entitlementError;
        }

        var conflict = await conflictStore.GetAsync(conflictId, context.RequestAborted).ConfigureAwait(false);
        if (conflict is null || !string.Equals(conflict.Value.ReplicaId, replicaId, StringComparison.Ordinal))
        {
            return NotFound(context, $"Conflict '{conflictId}' was not found for replica '{replicaId}'.");
        }

        var current = conflict.Value;
        var editAccessError = await RequireReplicaEditAccessAsync(context, current.ServiceId).ConfigureAwait(false);
        if (editAccessError is not null)
        {
            return editAccessError;
        }

        return Results.Json(ToConflictDetail(current), ReplicaConflictsJsonContext.Default.ConflictDetail);
    }

    private static async Task<IResult> HandleResolveConflictAsync(
        string replicaId,
        Guid conflictId,
        ResolveConflictRequest request,
        [FromServices] IReplicaConflictStore conflictStore,
        [FromServices] IAuditLog auditLog,
        HttpContext context)
    {
        var entitlementError = RequireEntitlement(context);
        if (entitlementError is not null)
        {
            return entitlementError;
        }

        if (!TryParseResolution(request.Resolution, out var resolution))
        {
            return BadRequest(context,
                "resolution must be one of: accept_client, keep_server, merge_fields, reject_client, defer.");
        }

        var conflict = await conflictStore.GetAsync(conflictId, context.RequestAborted).ConfigureAwait(false);
        if (conflict is null || !string.Equals(conflict.Value.ReplicaId, replicaId, StringComparison.Ordinal))
        {
            return NotFound(context, $"Conflict '{conflictId}' was not found for replica '{replicaId}'.");
        }

        var current = conflict.Value;
        var editAccessError = await RequireReplicaEditAccessAsync(context, current.ServiceId).ConfigureAwait(false);
        if (editAccessError is not null)
        {
            return editAccessError;
        }

        if (current.Resolution is not null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context, StatusCodes.Status409Conflict, "Conflict has already been resolved.");
        }

        if (resolution == ReplicaConflictResolution.MergeFields && string.IsNullOrWhiteSpace(request.MergedPayloadJson))
        {
            return BadRequest(context, "mergedPayloadJson is required when resolution is merge_fields.");
        }

        // Commit a new server state for resolutions that adopt a client/merged feature.
        var payloadToApply = resolution switch
        {
            ReplicaConflictResolution.AcceptClient => current.ClientPayloadJson,
            ReplicaConflictResolution.MergeFields => request.MergedPayloadJson,
            _ => null,
        };

        if (payloadToApply is not null)
        {
            var applyError = await ApplyResolvedFeatureAsync(context, current, payloadToApply).ConfigureAwait(false);
            if (applyError is not null)
            {
                return applyError;
            }
        }

        var resolvedBy = ResolveActor(context);
        var resolutionPayloadJson = resolution == ReplicaConflictResolution.MergeFields ? request.MergedPayloadJson : null;
        var resolved = await conflictStore.ResolveAsync(
            conflictId, resolution, resolvedBy, resolutionPayloadJson, context.RequestAborted).ConfigureAwait(false);
        if (!resolved)
        {
            // Lost a race with a concurrent resolution.
            return ProblemDetailsHelpers.CreateAdminProblem(
                context, StatusCodes.Status409Conflict, "Conflict has already been resolved.");
        }

        await auditLog.RecordAsync(
            new AuditEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                EventType = AuditEventType.AdminAction,
                Actor = resolvedBy,
                ActorType = AuditActorType.UserId,
                ResourceType = "replica_conflict",
                ResourceId = conflictId.ToString(),
                Action = "replica.conflict.resolve",
                Outcome = AuditOutcome.Success,
                CorrelationId = context.TraceIdentifier,
                Details = $"{{\"resolution\":\"{ResolutionToString(resolution)}\",\"replicaId\":\"{replicaId}\"}}",
            },
            context.RequestAborted).ConfigureAwait(false);

        return Results.Json(
            new ResolveConflictResponse
            {
                ConflictId = conflictId.ToString(),
                Resolution = ResolutionToString(resolution),
            },
            ReplicaConflictsJsonContext.Default.ResolveConflictResponse);
    }

    /// <summary>
    /// Applies a resolved feature (client or merged) through the canonical edit pipeline so the
    /// resolution reuses validation, change tracking, and audit hooks.
    /// </summary>
    private static async Task<IResult?> ApplyResolvedFeatureAsync(
        HttpContext context,
        ReplicaConflict conflict,
        string featureJson)
    {
        GeoServicesFeature? feature;
        try
        {
            feature = JsonSerializer.Deserialize(featureJson, FeatureServerJsonContext.Default.GeoServicesFeature);
        }
        catch (JsonException)
        {
            return BadRequest(context, "The resolution payload is not a valid feature.");
        }

        if (feature is null)
        {
            return BadRequest(context, "The resolution payload is not a valid feature.");
        }

        var editsHandler = context.RequestServices.GetRequiredService<FeatureServerEditsHandler>();
        var limits = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>();
        var editRequest = new ApplyEditsRequest { Updates = [feature], RollbackOnFailure = true };

        var editResult = await editsHandler.HandleApplyEditsAsync(
            conflict.ServiceId, conflict.LayerId, editRequest, limits.Value.Edits, context.RequestAborted)
            .ConfigureAwait(false);

        if (editResult is not JsonHttpResult<ApplyEditsResponse> jsonResult)
        {
            // The edit pipeline returned its own error result (e.g. validation); surface it.
            return editResult;
        }

        if (jsonResult.Value is not { } applyResponse ||
            !applyResponse.Success ||
            HasFailedResult(applyResponse.UpdateResults) ||
            HasFailedResult(applyResponse.AddResults) ||
            HasFailedResult(applyResponse.DeleteResults))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context, StatusCodes.Status409Conflict, "Failed to commit the resolved feature state.");
        }

        return null;
    }

    private static bool HasFailedResult(EditResult[]? results)
        => results is not null && Array.Exists(results, static result => !result.Success);

    private static ConflictDetail ToConflictDetail(ReplicaConflict conflict)
    {
        var clientFeature = ParseJsonOrNull(conflict.ClientPayloadJson);
        var serverFeature = ParseJsonOrNull(conflict.ServerPayloadJson);
        var baseFeature = ParseJsonOrNull(conflict.BasePayloadJson);
        var resolutionFeature = ParseJsonOrNull(conflict.ResolutionPayloadJson);

        return new ConflictDetail
        {
            ConflictId = conflict.ConflictId.ToString(),
            ReplicaId = conflict.ReplicaId,
            SyncOpId = conflict.SyncOpId.ToString(),
            ServiceId = conflict.ServiceId,
            LayerId = conflict.LayerId,
            ObjectId = conflict.ObjectId,
            ConflictType = ConflictTypeToString(conflict.ConflictType),
            BaseGeneration = conflict.BaseGeneration,
            ClientFeature = clientFeature,
            ServerFeature = serverFeature,
            BaseFeature = baseFeature,
            FieldChanges = BuildFieldChanges(baseFeature, clientFeature, serverFeature),
            GeometryChange = BuildGeometryChange(baseFeature, clientFeature, serverFeature),
            Resolution = conflict.Resolution is { } resolution ? ResolutionToString(resolution) : null,
            ResolvedBy = conflict.ResolvedBy,
            ResolvedAt = conflict.ResolvedAt,
            ResolutionFeature = resolutionFeature,
            CreatedAt = conflict.CreatedAt,
            UpdatedAt = conflict.UpdatedAt,
            TemporalHistoryHref =
                $"/api/v1/history/{conflict.ServiceId}/layers/{conflict.LayerId}/features/{conflict.ObjectId}",
        };
    }

    private static JsonElement? ParseJsonOrNull(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IResult? RequireEntitlement(HttpContext context)
        => LicenseGate.RequireEntitlement(context, ConflictReviewEntitlement, ConflictReviewFeatureName);

    private static Task<IResult?> RequireReplicaEditAccessAsync(HttpContext context, string serviceId)
        => ServiceDataEditorAuthorization.RequireServiceDataEditorAsync(
            context,
            serviceId,
            context.RequestAborted);

    private static string ResolveActor(HttpContext context)
        => context.User?.Identity?.Name ?? AuditEvent.AnonymousActor;

    private static FieldChange[] BuildFieldChanges(
        JsonElement? baseFeature,
        JsonElement? clientFeature,
        JsonElement? serverFeature)
    {
        var hasBaseFeature = IsObjectFeature(baseFeature);
        var baseAttributes = ExtractAttributes(baseFeature);
        var clientAttributes = ExtractAttributes(clientFeature);
        var serverAttributes = ExtractAttributes(serverFeature);

        var fieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        fieldNames.UnionWith(baseAttributes.Keys);
        fieldNames.UnionWith(clientAttributes.Keys);
        fieldNames.UnionWith(serverAttributes.Keys);

        var changes = new List<FieldChange>(fieldNames.Count);
        foreach (var fieldName in fieldNames.Order(StringComparer.OrdinalIgnoreCase))
        {
            var baseValue = baseAttributes.TryGetValue(fieldName, out var b) ? b : (JsonElement?)null;
            var clientValue = clientAttributes.TryGetValue(fieldName, out var c) ? c : (JsonElement?)null;
            var serverValue = serverAttributes.TryGetValue(fieldName, out var s) ? s : (JsonElement?)null;
            var clientDiffersFromServer = !JsonValueEquals(clientValue, serverValue);
            var clientChanged = hasBaseFeature ? !JsonValueEquals(baseValue, clientValue) : clientDiffersFromServer;
            var serverChanged = hasBaseFeature ? !JsonValueEquals(baseValue, serverValue) : clientDiffersFromServer;

            if (!clientChanged && !serverChanged && !clientDiffersFromServer)
            {
                continue;
            }

            changes.Add(new FieldChange
            {
                FieldName = fieldName,
                BaseValue = baseValue,
                ClientValue = clientValue,
                ServerValue = serverValue,
                ClientChanged = clientChanged,
                ServerChanged = serverChanged,
                ClientDiffersFromServer = clientDiffersFromServer,
            });
        }

        return changes.ToArray();
    }

    private static GeometryChangeMetadata BuildGeometryChange(
        JsonElement? baseFeature,
        JsonElement? clientFeature,
        JsonElement? serverFeature)
    {
        var hasBaseFeature = IsObjectFeature(baseFeature);
        var baseGeometry = ExtractGeometry(baseFeature);
        var clientGeometry = ExtractGeometry(clientFeature);
        var serverGeometry = ExtractGeometry(serverFeature);
        var clientDiffersFromServer = !JsonValueEquals(clientGeometry, serverGeometry);

        return new GeometryChangeMetadata
        {
            BaseHasGeometry = baseGeometry is not null,
            ClientHasGeometry = clientGeometry is not null,
            ServerHasGeometry = serverGeometry is not null,
            ClientChanged = hasBaseFeature ? !JsonValueEquals(baseGeometry, clientGeometry) : clientDiffersFromServer,
            ServerChanged = hasBaseFeature ? !JsonValueEquals(baseGeometry, serverGeometry) : clientDiffersFromServer,
            ClientDiffersFromServer = clientDiffersFromServer,
        };
    }

    private static Dictionary<string, JsonElement> ExtractAttributes(JsonElement? feature)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (feature is not { } value ||
            value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("attributes", out var attributes) ||
            attributes.ValueKind != JsonValueKind.Object)
        {
            return values;
        }

        foreach (var property in attributes.EnumerateObject())
        {
            values[property.Name] = property.Value.Clone();
        }

        return values;
    }

    private static JsonElement? ExtractGeometry(JsonElement? feature)
    {
        if (feature is not { } value ||
            value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("geometry", out var geometry) ||
            geometry.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return geometry.Clone();
    }

    private static bool IsObjectFeature(JsonElement? feature)
        => feature is { } value && value.ValueKind == JsonValueKind.Object;

    private static bool JsonValueEquals(JsonElement? left, JsonElement? right)
    {
        if (left is null && right is null)
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        var leftValue = left.Value;
        var rightValue = right.Value;
        if (leftValue.ValueKind != rightValue.ValueKind)
        {
            return false;
        }

        return string.Equals(leftValue.GetRawText(), rightValue.GetRawText(), StringComparison.Ordinal);
    }

    private static int ClampPageSize(int? requested, int max)
    {
        if (requested is not { } value || value <= 0)
        {
            return DefaultPageSize;
        }

        return Math.Min(value, max);
    }

    private static string? NormalizeFilter(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static IResult NotFound(HttpContext context, string detail)
        => ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound, detail);

    private static IResult BadRequest(HttpContext context, string detail)
        => ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, detail);

    private static string ConflictTypeToString(ReplicaConflictType type) => type switch
    {
        ReplicaConflictType.Attribute => "attribute",
        ReplicaConflictType.Geometry => "geometry",
        ReplicaConflictType.UpdateDelete => "update-delete",
        ReplicaConflictType.DeleteUpdate => "delete-update",
        ReplicaConflictType.DeleteDelete => "delete-delete",
        ReplicaConflictType.DuplicateInsert => "duplicate-insert",
        _ => "unknown",
    };

    private static string ResolutionToString(ReplicaConflictResolution resolution) => resolution switch
    {
        ReplicaConflictResolution.AcceptClient => "accept_client",
        ReplicaConflictResolution.KeepServer => "keep_server",
        ReplicaConflictResolution.MergeFields => "merge_fields",
        ReplicaConflictResolution.RejectClient => "reject_client",
        ReplicaConflictResolution.Deferred => "defer",
        _ => "unknown",
    };

    private static bool TryParseResolution(string? value, out ReplicaConflictResolution resolution)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "accept_client":
                resolution = ReplicaConflictResolution.AcceptClient;
                return true;
            case "keep_server":
                resolution = ReplicaConflictResolution.KeepServer;
                return true;
            case "merge_fields":
                resolution = ReplicaConflictResolution.MergeFields;
                return true;
            case "reject_client":
                resolution = ReplicaConflictResolution.RejectClient;
                return true;
            case "defer":
                resolution = ReplicaConflictResolution.Deferred;
                return true;
            default:
                resolution = default;
                return false;
        }
    }
}
