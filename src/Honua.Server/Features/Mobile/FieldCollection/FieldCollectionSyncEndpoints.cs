// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Mobile.FieldCollection;
using Honua.Core.Features.Mobile.FieldCollection.Abstractions;
using Honua.Core.Features.Mobile.FieldCollection.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Licensing;
using Honua.Infrastructure.Models;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Mobile.FieldCollection;

internal static class FieldCollectionSyncEndpoints
{
    internal const string ClientIdHeader = "X-Honua-Client-Id";
    private const string ProtocolTag = "fieldcollection-mobile-sync";
    private const int MaxClientIdLength = 128;
    // changeId and featureId are primary-key columns; bounding them keeps every
    // value well under the PostgreSQL B-tree row limit so oversize input is a 400.
    private const int MaxIdentifierLength = 256;
    private const string DefaultClientId = "default";
    private const int DefaultPullLimit = 200;
    private const int MaxPullLimit = 1_000;

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

        _ = group.Map("/sync-cursor", HandlePostSyncCursor)
            .WithName("AckFieldCollectionSyncCursor")
            .WithSummary("Acknowledge the per-client cursor after local persistence succeeds")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]));

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

        var entitlementGate = RequireOfflineSyncEntitlement(context);
        if (entitlementGate is not null)
        {
            return entitlementGate;
        }

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

        var entitlementGate = RequireOfflineSyncEntitlement(context);
        if (entitlementGate is not null)
        {
            return entitlementGate;
        }

        var clientResolution = ResolveClientId(context);
        if (clientResolution.Error is { } clientIdError)
        {
            return clientIdError;
        }

        var clientId = clientResolution.ClientId;

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

    private static async Task<IResult> HandlePostSyncCursor(
        HttpContext context,
        [FromBody] FieldCollectionSyncCursorAckRequest? body,
        [FromServices] IFieldCollectionSyncStore store,
        [FromServices] ILoggerFactory loggerFactory)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("FieldCollectionSync.SyncCursorAck");
        activity?.SetTag("honua.protocol", ProtocolTag);
        activity?.SetTag("honua.operation", "sync-cursor-ack");
        var logger = loggerFactory.CreateLogger(typeof(FieldCollectionSyncEndpoints));

        var entitlementGate = RequireOfflineSyncEntitlement(context);
        if (entitlementGate is not null)
        {
            return entitlementGate;
        }

        if (body?.LastSyncGeneration is not long lastSyncGeneration)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                "Field 'lastSyncGeneration' is required.");
        }

        if (lastSyncGeneration < 0)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                "Field 'lastSyncGeneration' must be greater than or equal to 0.");
        }

        var clientResolution = ResolveClientId(context);
        if (clientResolution.Error is { } clientIdError)
        {
            return clientIdError;
        }

        var clientId = clientResolution.ClientId;

        activity?.SetTag("honua.fieldcollection.client_id", clientId);
        activity?.SetTag("honua.fieldcollection.last_sync_generation", lastSyncGeneration);

        try
        {
            await store.RecordSyncCursorAsync(clientId, lastSyncGeneration, context.RequestAborted).ConfigureAwait(false);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Store rejected the value because it exceeds the committed server
            // watermark. Surface as 400 to the caller — never as a 5xx — so a
            // misbehaving client cannot poison the stored cursor.
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                "Field 'lastSyncGeneration' must not exceed the current server generation.");
        }

        var cursor = await store.GetSyncCursorAsync(clientId, context.RequestAborted).ConfigureAwait(false);
        FieldCollectionSyncLog.SyncCursorAcknowledged(logger, cursor.ClientId, cursor.LastSyncGeneration);

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

        var entitlementGate = RequireOfflineSyncEntitlement(context);
        if (entitlementGate is not null)
        {
            return entitlementGate;
        }

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

        var clientResolution = ResolveClientId(context);
        if (clientResolution.Error is { } clientIdError)
        {
            return clientIdError;
        }

        var clientId = clientResolution.ClientId;

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
        [FromServices] ILoggerFactory loggerFactory,
        [FromServices] FieldCollectionAutomationTrigger? automationTrigger = null)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("FieldCollectionSync.Push");
        activity?.SetTag("honua.protocol", ProtocolTag);
        activity?.SetTag("honua.operation", "push");
        var logger = loggerFactory.CreateLogger(typeof(FieldCollectionSyncEndpoints));

        var entitlementGate = RequireOfflineSyncEntitlement(context);
        if (entitlementGate is not null)
        {
            return entitlementGate;
        }

        if (ValidateRequiredPushFields(context, body) is { } requiredFieldError)
        {
            return requiredFieldError;
        }

        if (!TryParseOperation(body!.Operation, out var operation))
        {
            return BadRequest(context, "Field 'operation' must be one of: insert, update, delete.");
        }

        if (ValidatePushContent(context, body, operation) is { } contentError)
        {
            return contentError;
        }

        var clientResolution = ResolveClientId(context);
        if (clientResolution.Error is { } clientIdError)
        {
            return clientIdError;
        }

        var clientId = clientResolution.ClientId;

        // ValidateRequiredPushFields has rejected a missing layer id.
        var layerId = body.LayerId.GetValueOrDefault();
        activity?.SetTag("honua.fieldcollection.client_id", clientId);
        activity?.SetTag("honua.fieldcollection.layer_id", layerId);
        activity?.SetTag("honua.fieldcollection.change_operation", OperationToWire(operation));

        var pushRequest = new FieldCollectionPushRequest
        {
            ChangeId = body.ChangeId!.Trim(),
            FeatureId = body.FeatureId!.Trim(),
            LayerId = layerId,
            Operation = operation,
            BaseVersion = body.BaseVersion,
            Timestamp = body.Timestamp,
            FeaturePayloadJson = body.Feature,
        };

        var result = await store.PushChangeAsync(clientId, pushRequest, context.RequestAborted).ConfigureAwait(false);
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

        // Server-side Workflows companion: fan out online actions only for
        // durably applied changes. Best-effort — the trigger swallows its own
        // failures so automation never affects the push outcome returned above.
        if (automationTrigger is not null
            && result.Outcome == FieldCollectionPushOutcome.Applied
            && result.Version is long appliedVersion)
        {
            var automationEvent = new FieldCollectionAutomationEvent
            {
                ClientId = clientId,
                ChangeId = result.ChangeId,
                FeatureId = pushRequest.FeatureId,
                LayerId = pushRequest.LayerId,
                Operation = pushRequest.Operation,
                Version = appliedVersion,
                Generation = result.ServerGeneration,
                Timestamp = pushRequest.Timestamp ?? DateTimeOffset.UtcNow,
                FeaturePayloadJson = pushRequest.FeaturePayloadJson,
            };

            await automationTrigger.OnChangeAppliedAsync(automationEvent, context.RequestAborted).ConfigureAwait(false);
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

    // API-key fallback principals share the admin name, so device-specific cursors require X-Honua-Client-Id.
    // An over-long header is rejected rather than truncated: truncation would let two devices whose
    // ids share a prefix share one cursor and one idempotency scope.
    private static ClientIdResolution ResolveClientId(HttpContext context)
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
                return trimmed.Length > MaxClientIdLength
                    ? new ClientIdResolution(
                        string.Empty,
                        BadRequest(context, $"Header '{ClientIdHeader}' must be at most {MaxClientIdLength} characters."))
                    : new ClientIdResolution(trimmed, null);
            }
        }

        var name = context.User?.Identity?.Name;
        return new ClientIdResolution(string.IsNullOrWhiteSpace(name) ? DefaultClientId : name.Trim(), null);
    }

    private readonly record struct ClientIdResolution(string ClientId, IResult? Error);

    private static IResult BadRequest(HttpContext context, string detail)
        => ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, detail);

    private static IResult? ValidateRequiredPushFields(HttpContext context, FieldCollectionPushRequestModel? body)
    {
        if (body is null)
        {
            return BadRequest(context, "Request body is required.");
        }

        if (string.IsNullOrWhiteSpace(body.ChangeId))
        {
            return BadRequest(context, "Field 'changeId' is required.");
        }

        if (string.IsNullOrWhiteSpace(body.FeatureId))
        {
            return BadRequest(context, "Field 'featureId' is required.");
        }

        return body.LayerId is null ? BadRequest(context, "Field 'layerId' is required.") : null;
    }

    private static IResult? ValidatePushContent(
        HttpContext context,
        FieldCollectionPushRequestModel body,
        FieldCollectionChangeOperation operation)
    {
        // The published contract states that 'feature' must be null for delete
        // operations. Enforcing it here keeps mobile clients honest about the
        // wire shape and prevents the store from silently dropping a payload
        // the caller intended to ship.
        if (operation == FieldCollectionChangeOperation.Delete && body.Feature is not null)
        {
            return BadRequest(context, "Field 'feature' must be null for delete operations.");
        }

        var identifierError = ValidateIdentifier(context, "changeId", body.ChangeId!)
            ?? ValidateIdentifier(context, "featureId", body.FeatureId!);
        if (identifierError is not null)
        {
            return identifierError;
        }

        // PostgreSQL jsonb cannot store U+0000 or an unpaired surrogate; reject them here
        // instead of letting the insert fail with a 500.
        return body.Feature is not null && FeaturePayloadHasUnstorableText(body.Feature)
            ? BadRequest(context, "Field 'feature' must not contain NUL (U+0000) characters or unpaired surrogate escapes.")
            : null;
    }

    private static IResult? ValidateIdentifier(HttpContext context, string field, string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length > MaxIdentifierLength)
        {
            return BadRequest(context, $"Field '{field}' must be at most {MaxIdentifierLength} characters.");
        }

        return trimmed.Contains('\0', StringComparison.Ordinal)
            ? BadRequest(context, $"Field '{field}' must not contain NUL (U+0000) characters.")
            : null;
    }

    private static bool FeaturePayloadHasUnstorableText(string featureJson)
    {
        // The raw payload keeps JSON escapes, and valid JSON cannot carry a raw NUL or a
        // lone surrogate, so either can only arrive as a "\u" escape.
        if (!featureJson.Contains("\\u", StringComparison.Ordinal))
        {
            return false;
        }

        using var document = JsonDocument.Parse(featureJson);
        try
        {
            return ContainsNul(document.RootElement);
        }
        catch (InvalidOperationException)
        {
            // Unescaping an unpaired surrogate (for example "\ud800") fails; jsonb rejects it too.
            return true;
        }
    }

    private static bool ContainsNul(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString()!.Contains('\0', StringComparison.Ordinal);
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (ContainsNul(item))
                    {
                        return true;
                    }
                }

                return false;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name.Contains('\0', StringComparison.Ordinal) || ContainsNul(property.Value))
                    {
                        return true;
                    }
                }

                return false;
            default:
                return false;
        }
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

    private static IResult? RequireOfflineSyncEntitlement(HttpContext context)
        => LicenseGate.RequireEntitlement(
            context,
            FeatureCatalog.FieldOpsOfflineSyncKey,
            "Offline field sync");
}
