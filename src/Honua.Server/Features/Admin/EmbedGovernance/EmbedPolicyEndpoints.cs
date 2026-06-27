// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.EmbedGovernance.Abstractions;
using Honua.Core.Features.EmbedGovernance.Domain;
using Honua.Server.Features.Admin.EmbedGovernance.Models;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin.EmbedGovernance;

/// <summary>
/// Public embed governance endpoints: the policy endpoint consumed by the
/// <c>@honua-io/embed</c> remote governance adapter, and redacted analytics
/// ingestion. Both authenticate via the embed key presented in the
/// <c>X-Honua-Embed-Key</c> header (or <c>key</c> query parameter); the server
/// is the authoritative origin/domain, scope, and rate-limit boundary.
/// </summary>
internal static partial class EmbedPolicyEndpoints
{
    internal sealed class EmbedPolicyEndpointsLog;

    private const string EmbedKeyHeader = "X-Honua-Embed-Key";

    /// <summary>
    /// Registers the public embed policy and analytics ingestion endpoints.
    /// </summary>
    /// <param name="endpoints">The route builder.</param>
    public static void MapEmbedPolicyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/embed")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Embed Governance")
            .AllowAnonymous();

        group.MapGet("/policy", HandlePolicy)
            .WithDisplayName("Fetch Embed Policy")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPost("/analytics", HandleAnalytics)
            .WithDisplayName("Ingest Embed Analytics")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));
    }

    private static async Task<Results<Ok<EmbedPolicyResponse>, UnauthorizedHttpResult, JsonHttpResult<ApiResponse<object>>>>
        HandlePolicy(
            [FromServices] IEmbedKeyStore store,
            [FromServices] IEmbedAnalyticsStore analytics,
            [FromServices] IAuditLog auditLog,
            [FromServices] ILogger<EmbedPolicyEndpointsLog> logger,
            [FromQuery] string? serviceId,
            [FromQuery] string? contentId,
            [FromQuery] string? tenantId,
            HttpContext context)
    {
        var validation = await ResolveKeyAsync(store, context);
        if (validation is null)
        {
            return TypedResults.Unauthorized();
        }

        var key = validation.Record;
        var origin = context.Request.Headers.Origin.ToString();
        var consumed = await store.RecordRequestAsync(key.Id, key.Scope.RateLimitWindow, context.RequestAborted);

        var request = new EmbedPolicyRequest
        {
            Origin = origin,
            ServiceId = serviceId,
            ContentId = contentId,
            TenantId = tenantId,
            RequestsConsumedInWindow = consumed,
        };

        var decision = EmbedPolicyEvaluator.Evaluate(key, request, DateTimeOffset.UtcNow);
        if (!decision.Allowed)
        {
            LogPolicyDenied(logger, key.Id, decision.Reason);
            await IngestDenialAsync(analytics, key, request, decision, context.RequestAborted);
            await EmitDenialAuditAsync(auditLog, context, key.Id, decision);

            var failure = ApiResponse<object>.Failure($"embed policy denied: {decision.Reason}");
            return TypedResults.Json(failure, statusCode: StatusCodes.Status403Forbidden);
        }

        // Authoritative server-side CORS echo for the approved origin.
        if (!string.IsNullOrWhiteSpace(origin))
        {
            context.Response.Headers["Access-Control-Allow-Origin"] = origin;
            context.Response.Headers.Append("Vary", "Origin");
        }

        var policy = EmbedPolicyEvaluator.BuildPolicy(key);
        return TypedResults.Ok(ToResponse(policy));
    }

    private static async Task<Results<Ok<ApiResponse<EmbedAnalyticsIngestResponse>>, UnauthorizedHttpResult, BadRequest<ApiResponse<object>>>>
        HandleAnalytics(
            IngestEmbedAnalyticsRequest request,
            [FromServices] IEmbedKeyStore store,
            [FromServices] IEmbedAnalyticsStore analytics,
            HttpContext context)
    {
        var validation = await ResolveKeyAsync(store, context);
        if (validation is null)
        {
            return TypedResults.Unauthorized();
        }

        if (request?.Events is null || request.Events.Count == 0)
        {
            return TypedResults.BadRequest(ApiResponse<object>.Failure("at least one analytics event is required"));
        }

        var key = validation.Record;
        var now = DateTimeOffset.UtcNow;
        var accepted = 0;

        foreach (var dto in request.Events)
        {
            if (!TryMapEvent(dto, key, now, out var analyticsEvent, out var mapError))
            {
                return TypedResults.BadRequest(ApiResponse<object>.Failure(mapError));
            }

            var result = EmbedAnalyticsValidator.Validate(analyticsEvent);
            if (!result.IsValid)
            {
                return TypedResults.BadRequest(ApiResponse<object>.Failure(result.Error ?? "invalid analytics event"));
            }

            await analytics.IngestAsync(analyticsEvent, context.RequestAborted);
            accepted++;
        }

        var response = new EmbedAnalyticsIngestResponse { Accepted = accepted };
        return TypedResults.Ok(ApiResponse<EmbedAnalyticsIngestResponse>.CreateSuccess(response));
    }

    private static async Task<EmbedKeyValidationResult?> ResolveKeyAsync(IEmbedKeyStore store, HttpContext context)
    {
        var keyMaterial = ExtractKeyMaterial(context);
        if (string.IsNullOrWhiteSpace(keyMaterial))
        {
            return null;
        }

        return await store.ValidateAsync(keyMaterial, context.RequestAborted);
    }

    private static string? ExtractKeyMaterial(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(EmbedKeyHeader, out var header))
        {
            var value = header.ToString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        var query = context.Request.Query["key"].ToString();
        return string.IsNullOrWhiteSpace(query) ? null : query.Trim();
    }

    private static bool TryMapEvent(
        EmbedAnalyticsEventDto dto,
        EmbedKeyRecord key,
        DateTimeOffset now,
        out EmbedAnalyticsEvent analyticsEvent,
        out string error)
    {
        analyticsEvent = null!;
        error = string.Empty;

        if (dto is null)
        {
            error = "analytics event is required";
            return false;
        }

        if (!Enum.TryParse<EmbedAnalyticsEventType>(dto.EventType, ignoreCase: true, out var eventType)
            || !Enum.IsDefined(eventType))
        {
            error = $"unknown eventType '{dto.EventType}'";
            return false;
        }

        EmbedPolicyDenyReason? denyReason = null;
        if (!string.IsNullOrWhiteSpace(dto.DenyReason))
        {
            if (!Enum.TryParse<EmbedPolicyDenyReason>(dto.DenyReason, ignoreCase: true, out var parsedReason)
                || !Enum.IsDefined(parsedReason))
            {
                error = $"unknown denyReason '{dto.DenyReason}'";
                return false;
            }

            denyReason = parsedReason;
        }

        analyticsEvent = new EmbedAnalyticsEvent
        {
            EventType = eventType,
            KeyId = key.Id,
            IntegrationId = string.IsNullOrWhiteSpace(dto.IntegrationId) ? key.Scope.IntegrationId : dto.IntegrationId.Trim(),
            TenantId = string.IsNullOrWhiteSpace(dto.TenantId) ? key.Scope.TenantId : dto.TenantId.Trim(),
            Origin = EmbedPolicyEvaluator.NormalizeOrigin(dto.Origin),
            ServiceId = string.IsNullOrWhiteSpace(dto.ServiceId) ? null : dto.ServiceId.Trim(),
            LayerId = string.IsNullOrWhiteSpace(dto.LayerId) ? null : dto.LayerId.Trim(),
            DenyReason = denyReason,
            OccurredAt = dto.OccurredAt ?? now,
        };

        return true;
    }

    private static Task IngestDenialAsync(
        IEmbedAnalyticsStore analytics,
        EmbedKeyRecord key,
        EmbedPolicyRequest request,
        EmbedPolicyDecision decision,
        CancellationToken cancellationToken)
    {
        var analyticsEvent = new EmbedAnalyticsEvent
        {
            EventType = EmbedAnalyticsEventType.PolicyDenial,
            KeyId = key.Id,
            IntegrationId = key.Scope.IntegrationId,
            TenantId = key.Scope.TenantId,
            Origin = EmbedPolicyEvaluator.NormalizeOrigin(request.Origin),
            ServiceId = string.IsNullOrWhiteSpace(request.ServiceId) ? null : request.ServiceId.Trim(),
            LayerId = string.IsNullOrWhiteSpace(request.ContentId) ? null : request.ContentId.Trim(),
            DenyReason = decision.Reason,
            OccurredAt = DateTimeOffset.UtcNow,
        };

        return analytics.IngestAsync(analyticsEvent, cancellationToken);
    }

    private static Task EmitDenialAuditAsync(
        IAuditLog auditLog,
        HttpContext context,
        Guid keyId,
        EmbedPolicyDecision decision)
    {
        var auditEvent = new AuditEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            EventType = AuditEventType.Authorization,
            Actor = $"embed-key:{keyId}",
            ActorType = AuditActorType.ApiKey,
            ResourceType = "embed_policy",
            ResourceId = keyId.ToString(),
            Action = "embed_policy.deny",
            Outcome = AuditOutcome.Denied,
            CorrelationId = context.TraceIdentifier,
            Details = decision.Reason.ToString(),
        };

        return auditLog.RecordAsync(auditEvent, context.RequestAborted);
    }

    private static EmbedPolicyResponse ToResponse(EmbedPolicy policy) => new()
    {
        IntegrationId = policy.IntegrationId,
        TenantId = policy.TenantId,
        Edition = policy.Edition,
        AllowedOrigins = policy.AllowedOrigins,
        AllowedServices = policy.AllowedServices,
        AllowedContentIds = policy.AllowedContentIds,
        Capabilities = policy.Capabilities,
        RateLimit = new EmbedRateLimitResponse
        {
            RequestsPerWindow = policy.RateLimit.RequestsPerWindow,
            WindowSeconds = policy.RateLimit.WindowSeconds,
        },
    };

    [LoggerMessage(EventId = 4610, Level = LogLevel.Information,
        Message = "Denied embed policy for key {EmbedKeyId}: {Reason}")]
    private static partial void LogPolicyDenied(ILogger logger, Guid embedKeyId, EmbedPolicyDenyReason reason);
}
