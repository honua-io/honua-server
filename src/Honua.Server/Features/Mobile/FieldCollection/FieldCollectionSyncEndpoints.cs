// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using Honua.Core.Features.Mobile.FieldCollection.Abstractions;
using Honua.Core.Features.Mobile.FieldCollection.Domain;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Mobile.FieldCollection;

/// <summary>
/// FieldCollection mobile sync endpoints (#894). Exposes generation, sync-cursor,
/// pull, and push paths consumed by the <c>honua-mobile</c> FieldCollection
/// offline sync clients.
/// </summary>
internal static class FieldCollectionSyncEndpoints
{
    internal const string ClientIdHeader = "X-Honua-Client-Id";
    private const string ProtocolTag = "fieldcollection-mobile-sync";
    private const int MaxClientIdLength = 128;
    private const string DefaultClientId = "default";
    private const int DefaultPullLimit = 200;
    private const int MaxPullLimit = 1_000;

    /// <summary>
    /// Maps the four FieldCollection mobile sync endpoints to the supplied app.
    /// </summary>
    /// <remarks>
    /// No-op when <see cref="IFieldCollectionSyncStore"/> is not registered, which
    /// is the case for non-Postgres data-source profiles (DuckDB, MySQL/MariaDB,
    /// SQL Server). Without this gate, requests would fail with an internal DI
    /// resolution error instead of a deliberate 404. Registration is probed via
    /// <see cref="IServiceProviderIsService"/> so that the scoped store is not
    /// constructed from the root provider during route mapping.
    /// </remarks>
    public static void MapFieldCollectionSyncEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var serviceInspector = app.Services.GetService<IServiceProviderIsService>();
        if (serviceInspector is not null && !serviceInspector.IsService(typeof(IFieldCollectionSyncStore)))
        {
            return;
        }

        var group = app.MapGroup("/api/v{version:apiVersion}/fieldcollection")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Mobile", "FieldCollection")
            .WithDescription("FieldCollection mobile sync endpoints (#894)")
            .RequireAdminAuthorization();

        _ = group.Map("/generation", HandleGetGeneration)
            .WithName("GetFieldCollectionGeneration")
            .WithSummary("Get latest server FieldCollection generation cursor")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]));

        _ = group.Map("/sync-cursor", HandleGetSyncCursor)
            .WithName("GetFieldCollectionSyncCursor")
            .WithSummary("Get last server-acknowledged generation for the calling client")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]));

        _ = group.Map("/changes", HandleGetChanges)
            .WithName("GetFieldCollectionChanges")
            .WithSummary("Pull ordered FieldCollection changes after a generation cursor")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]));

        _ = group.Map("/changes", HandlePostChange)
            .WithName("PushFieldCollectionChange")
            .WithSummary("Push a single FieldCollection change with idempotent outcome semantics")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]));
    }

    private static async Task<IResult> HandleGetGeneration(
        HttpContext context,
        [FromServices] IFieldCollectionSyncStore store,
        [FromServices] ILoggerFactory loggerFactory)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("FieldCollectionSync.Generation");
        activity?.SetTag("honua.protocol", ProtocolTag);
        activity?.SetTag("honua.operation", "generation");
        var logger = loggerFactory.CreateLogger(typeof(FieldCollectionSyncEndpoints));

        var serverGeneration = await store.GetCurrentGenerationAsync(context.RequestAborted).ConfigureAwait(false);
        activity?.SetTag("honua.fieldcollection.server_generation", serverGeneration);
        FieldCollectionSyncLog.GenerationServed(logger, serverGeneration);

        ApplyOfflineCacheHeaders(context.Response);
        return Results.Json(
            new FieldCollectionGenerationResponse { ServerGeneration = serverGeneration },
            FieldCollectionSyncJsonContext.Default.FieldCollectionGenerationResponse);
    }

    private static async Task<IResult> HandleGetSyncCursor(
        HttpContext context,
        [FromServices] IFieldCollectionSyncStore store,
        [FromServices] ILoggerFactory loggerFactory)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("FieldCollectionSync.SyncCursor");
        activity?.SetTag("honua.protocol", ProtocolTag);
        activity?.SetTag("honua.operation", "sync-cursor");
        var logger = loggerFactory.CreateLogger(typeof(FieldCollectionSyncEndpoints));

        var clientId = ResolveClientId(context);
        activity?.SetTag("honua.fieldcollection.client_id", clientId);
        var cursor = await store.GetSyncCursorAsync(clientId, context.RequestAborted).ConfigureAwait(false);
        activity?.SetTag("honua.fieldcollection.last_sync_generation", cursor.LastSyncGeneration);
        FieldCollectionSyncLog.SyncCursorServed(logger, cursor.ClientId, cursor.LastSyncGeneration);

        ApplyOfflineCacheHeaders(context.Response);
        return Results.Json(
            new FieldCollectionSyncCursorResponse
            {
                ClientId = cursor.ClientId,
                LastSyncGeneration = cursor.LastSyncGeneration,
            },
            FieldCollectionSyncJsonContext.Default.FieldCollectionSyncCursorResponse);
    }

    private static async Task<IResult> HandleGetChanges(
        HttpContext context,
        [FromServices] IFieldCollectionSyncStore store,
        [FromServices] ILoggerFactory loggerFactory,
        long? sinceGeneration = null,
        int? limit = null)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("FieldCollectionSync.Pull");
        activity?.SetTag("honua.protocol", ProtocolTag);
        activity?.SetTag("honua.operation", "pull");
        var logger = loggerFactory.CreateLogger(typeof(FieldCollectionSyncEndpoints));

        var since = sinceGeneration ?? 0L;
        if (since < 0)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                "Query parameter 'sinceGeneration' must be greater than or equal to 0.");
        }

        var effectiveLimit = limit ?? DefaultPullLimit;
        if (effectiveLimit <= 0 || effectiveLimit > MaxPullLimit)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                $"Query parameter 'limit' must be between 1 and {MaxPullLimit}.");
        }

        var clientId = ResolveClientId(context);
        activity?.SetTag("honua.fieldcollection.client_id", clientId);
        activity?.SetTag("honua.fieldcollection.since_generation", since);
        activity?.SetTag("honua.fieldcollection.limit", effectiveLimit);

        var page = await store.GetChangesAsync(clientId, since, effectiveLimit, context.RequestAborted).ConfigureAwait(false);

        activity?.SetTag("honua.fieldcollection.returned", page.Changes.Count);
        activity?.SetTag("honua.fieldcollection.has_more", page.HasMore);
        activity?.SetTag("honua.fieldcollection.server_generation", page.ServerGeneration);

        var changes = new FieldCollectionServerChange[page.Changes.Count];
        for (var i = 0; i < page.Changes.Count; i++)
        {
            var change = page.Changes[i];
            changes[i] = new FieldCollectionServerChange
            {
                FeatureId = change.FeatureId,
                LayerId = change.LayerId,
                Operation = OperationToWire(change.Operation),
                Version = change.Version,
                Generation = change.Generation,
                Timestamp = change.Timestamp,
                Feature = change.FeaturePayloadJson,
            };
        }

        var response = new FieldCollectionPullResponse
        {
            ServerGeneration = page.ServerGeneration,
            NextCursor = page.NextCursor,
            HasMore = page.HasMore,
            Changes = changes,
        };

        FieldCollectionSyncLog.PullServed(
            logger,
            clientId,
            since,
            effectiveLimit,
            changes.Length,
            page.HasMore,
            page.ServerGeneration);

        ApplyOfflineCacheHeaders(context.Response);
        return Results.Json(response, FieldCollectionSyncJsonContext.Default.FieldCollectionPullResponse);
    }

    private static async Task<IResult> HandlePostChange(
        HttpContext context,
        [FromBody] FieldCollectionPushRequestModel? body,
        [FromServices] IFieldCollectionSyncStore store,
        [FromServices] ILoggerFactory loggerFactory)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("FieldCollectionSync.Push");
        activity?.SetTag("honua.protocol", ProtocolTag);
        activity?.SetTag("honua.operation", "push");
        var logger = loggerFactory.CreateLogger(typeof(FieldCollectionSyncEndpoints));

        if (body is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                "Request body is required.");
        }

        if (string.IsNullOrWhiteSpace(body.ChangeId))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                "Field 'changeId' is required.");
        }

        if (string.IsNullOrWhiteSpace(body.FeatureId))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                "Field 'featureId' is required.");
        }

        if (body.LayerId is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                "Field 'layerId' is required.");
        }

        if (!TryParseOperation(body.Operation, out var operation))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                "Field 'operation' must be one of: insert, update, delete.");
        }

        var clientId = ResolveClientId(context);
        activity?.SetTag("honua.fieldcollection.client_id", clientId);
        activity?.SetTag("honua.fieldcollection.layer_id", body.LayerId.Value);
        activity?.SetTag("honua.fieldcollection.change_operation", OperationToWire(operation));

        var pushRequest = new FieldCollectionPushRequest
        {
            ChangeId = body.ChangeId!.Trim(),
            FeatureId = body.FeatureId!.Trim(),
            LayerId = body.LayerId.Value,
            Operation = operation,
            BaseVersion = body.BaseVersion,
            Timestamp = body.Timestamp,
            FeaturePayloadJson = body.Feature,
        };

        var result = await store.PushChangeAsync(pushRequest, context.RequestAborted).ConfigureAwait(false);
        activity?.SetTag("honua.fieldcollection.outcome", OutcomeToWire(result.Outcome));
        activity?.SetTag("honua.fieldcollection.server_generation", result.ServerGeneration);

        var response = new FieldCollectionPushResponse
        {
            ChangeId = result.ChangeId,
            Outcome = OutcomeToWire(result.Outcome),
            ServerGeneration = result.ServerGeneration,
            Version = result.Version,
            ConflictType = result.Outcome == FieldCollectionPushOutcome.Conflict
                ? ConflictTypeToWire(result.ConflictType)
                : null,
            ServerVersion = result.ServerVersion,
            ServerFeature = result.ServerFeaturePayloadJson,
            RejectionReason = result.RejectionReason,
        };

        if (result.Outcome == FieldCollectionPushOutcome.Rejected)
        {
            FieldCollectionSyncLog.PushRejected(logger, response.ChangeId, response.RejectionReason ?? string.Empty);
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            var operationWire = OperationToWire(pushRequest.Operation);
            FieldCollectionSyncLog.PushProcessed(
                logger,
                clientId,
                response.ChangeId,
                pushRequest.FeatureId,
                pushRequest.LayerId,
                operationWire,
                response.Outcome,
                response.ServerGeneration);
        }

        ApplyOfflineCacheHeaders(context.Response);
        return Results.Json(
            response,
            FieldCollectionSyncJsonContext.Default.FieldCollectionPushResponse);
    }

    /// <summary>
    /// Resolves a stable per-client identifier for sync-cursor partitioning.
    /// Mobile clients should send <c>X-Honua-Client-Id</c> on every sync call so
    /// that multiple field devices sharing one API key keep independent cursors.
    /// When the header is absent (legacy callers, server-to-server probes,
    /// admin clients) the principal name from <see cref="ApiKeyAuthenticationHandler"/>
    /// is used; that handler emits a single <c>"admin"</c> name, so callers in
    /// this branch share one cursor by design.
    /// </summary>
    private static string ResolveClientId(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(ClientIdHeader, out var headerValues))
        {
            for (var i = 0; i < headerValues.Count; i++)
            {
                var raw = headerValues[i];
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                var trimmed = raw.Trim();
                if (trimmed.Length > MaxClientIdLength)
                {
                    trimmed = trimmed[..MaxClientIdLength];
                }

                return trimmed;
            }
        }

        var name = context.User?.Identity?.Name;
        return string.IsNullOrWhiteSpace(name) ? DefaultClientId : name.Trim();
    }

    private static bool TryParseOperation(string? raw, out FieldCollectionChangeOperation operation)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            operation = default;
            return false;
        }

        switch (raw.Trim().ToLowerInvariant())
        {
            case "insert":
            case "create":
                operation = FieldCollectionChangeOperation.Insert;
                return true;
            case "update":
            case "modify":
                operation = FieldCollectionChangeOperation.Update;
                return true;
            case "delete":
            case "remove":
                operation = FieldCollectionChangeOperation.Delete;
                return true;
            default:
                if (short.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric)
                    && Enum.IsDefined(typeof(FieldCollectionChangeOperation), numeric))
                {
                    operation = (FieldCollectionChangeOperation)numeric;
                    return true;
                }

                operation = default;
                return false;
        }
    }

    private static string OperationToWire(FieldCollectionChangeOperation operation) => operation switch
    {
        FieldCollectionChangeOperation.Insert => "insert",
        FieldCollectionChangeOperation.Update => "update",
        FieldCollectionChangeOperation.Delete => "delete",
        _ => "update",
    };

    private static string OutcomeToWire(FieldCollectionPushOutcome outcome) => outcome switch
    {
        FieldCollectionPushOutcome.Applied => "applied",
        FieldCollectionPushOutcome.Conflict => "conflict",
        FieldCollectionPushOutcome.Rejected => "rejected",
        _ => "rejected",
    };

    private static string? ConflictTypeToWire(FieldCollectionConflictType conflictType) => conflictType switch
    {
        FieldCollectionConflictType.UpdateUpdate => "update-update",
        FieldCollectionConflictType.UpdateDelete => "update-delete",
        FieldCollectionConflictType.DeleteUpdate => "delete-update",
        FieldCollectionConflictType.DeleteDelete => "delete-delete",
        FieldCollectionConflictType.None => null,
        _ => null,
    };

    private static void ApplyOfflineCacheHeaders(HttpResponse response)
    {
        // Generation values mutate as soon as a write lands; offline clients
        // must not cache stale data on intermediate proxies.
        response.Headers.CacheControl = "no-store";
    }
}
