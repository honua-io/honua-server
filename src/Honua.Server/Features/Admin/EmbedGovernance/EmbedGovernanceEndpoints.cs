// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
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
/// Admin endpoints for embed API key lifecycle and embed usage reporting.
/// </summary>
internal static partial class EmbedGovernanceEndpoints
{
    internal sealed class EmbedGovernanceEndpointsLog;

    /// <summary>
    /// Registers embed governance admin endpoints.
    /// </summary>
    /// <param name="endpoints">The route builder.</param>
    public static void MapEmbedGovernanceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var keys = endpoints.MapGroup("/api/v{version:apiVersion}/admin/embed/keys")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Embed Governance")
            .RequireAdminAuthorization();

        keys.MapGet("/", HandleListKeys)
            .WithDisplayName("List Embed Keys")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        keys.MapPost("/", HandleCreateKey)
            .WithDisplayName("Create Embed Key")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        keys.MapGet("/{id:guid}", HandleGetKey)
            .WithDisplayName("Get Embed Key")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        keys.MapPost("/{id:guid}/rotate", HandleRotateKey)
            .WithDisplayName("Rotate Embed Key")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        keys.MapPost("/{id:guid}/revoke", HandleRevokeKey)
            .WithDisplayName("Revoke Embed Key")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        var usage = endpoints.MapGroup("/api/v{version:apiVersion}/admin/embed/usage")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Embed Governance")
            .RequireAdminAuthorization();

        usage.MapGet("/", HandleUsage)
            .WithDisplayName("Query Embed Usage")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
    }

    private static async Task<Ok<ApiResponse<IReadOnlyList<EmbedKeyResponse>>>> HandleListKeys(
        [FromServices] IEmbedKeyStore store,
        HttpContext context)
    {
        var now = DateTimeOffset.UtcNow;
        var records = await store.ListAsync(context.RequestAborted);
        var response = records.Select(record => ToResponse(record, now)).ToList().AsReadOnly();
        return TypedResults.Ok(ApiResponse<IReadOnlyList<EmbedKeyResponse>>.CreateSuccess(response));
    }

    private static async Task<Results<Created<ApiResponse<EmbedKeySecretResponse>>, BadRequest<ApiResponse<object>>>>
        HandleCreateKey(
            CreateEmbedKeyRequest request,
            [FromServices] IEmbedKeyStore store,
            [FromServices] IAuditLog auditLog,
            [FromServices] ILogger<EmbedGovernanceEndpointsLog> logger,
            HttpContext context)
    {
        if (!TryBuildScope(request, out var scope, out var validationMessage))
        {
            return TypedResults.BadRequest(ApiResponse<object>.Failure(validationMessage));
        }

        var creator = ResolveActor(context);
        var result = await store.CreateAsync(request.Name.Trim(), scope, request.ExpiresAt, creator, context.RequestAborted);

        LogKeyCreated(logger, result.Record.Id, result.Record.Name);
        await EmitAuditAsync(auditLog, context, AuditEventType.AdminAction, "embed_key.create",
            result.Record.Id.ToString(), AuditOutcome.Success, creator);

        var response = new EmbedKeySecretResponse
        {
            EmbedKey = ToResponse(result.Record, DateTimeOffset.UtcNow),
            Key = result.Key,
        };

        return TypedResults.Created(
            $"/api/v1/admin/embed/keys/{result.Record.Id}",
            ApiResponse<EmbedKeySecretResponse>.CreateSuccess(response));
    }

    private static async Task<Results<Ok<ApiResponse<EmbedKeyResponse>>, NotFound<ApiResponse<object>>>> HandleGetKey(
        Guid id,
        [FromServices] IEmbedKeyStore store,
        HttpContext context)
    {
        var record = await store.GetAsync(id, context.RequestAborted);
        if (record is null)
        {
            return TypedResults.NotFound(ApiResponse<object>.Failure("Embed key not found"));
        }

        return TypedResults.Ok(ApiResponse<EmbedKeyResponse>.CreateSuccess(ToResponse(record, DateTimeOffset.UtcNow)));
    }

    private static async Task<Results<Ok<ApiResponse<EmbedKeySecretResponse>>, NotFound<ApiResponse<object>>>>
        HandleRotateKey(
            Guid id,
            [FromServices] IEmbedKeyStore store,
            [FromServices] IAuditLog auditLog,
            [FromServices] ILogger<EmbedGovernanceEndpointsLog> logger,
            HttpContext context)
    {
        var result = await store.RotateAsync(id, context.RequestAborted);
        if (result is null)
        {
            return TypedResults.NotFound(ApiResponse<object>.Failure("Embed key not found or revoked"));
        }

        LogKeyRotated(logger, id);
        await EmitAuditAsync(auditLog, context, AuditEventType.AdminAction, "embed_key.rotate",
            id.ToString(), AuditOutcome.Success, ResolveActor(context));

        var response = new EmbedKeySecretResponse
        {
            EmbedKey = ToResponse(result.Record, DateTimeOffset.UtcNow),
            Key = result.Key,
        };

        return TypedResults.Ok(ApiResponse<EmbedKeySecretResponse>.CreateSuccess(response));
    }

    private static async Task<Results<Ok<ApiResponse<EmbedKeyResponse>>, NotFound<ApiResponse<object>>>> HandleRevokeKey(
        Guid id,
        [FromServices] IEmbedKeyStore store,
        [FromServices] IAuditLog auditLog,
        [FromServices] ILogger<EmbedGovernanceEndpointsLog> logger,
        HttpContext context)
    {
        var record = await store.RevokeAsync(id, context.RequestAborted);
        if (record is null)
        {
            return TypedResults.NotFound(ApiResponse<object>.Failure("Embed key not found"));
        }

        LogKeyRevoked(logger, id);
        await EmitAuditAsync(auditLog, context, AuditEventType.AdminAction, "embed_key.revoke",
            id.ToString(), AuditOutcome.Success, ResolveActor(context));

        return TypedResults.Ok(ApiResponse<EmbedKeyResponse>.CreateSuccess(ToResponse(record, DateTimeOffset.UtcNow)));
    }

    private static async Task<Results<Ok<ApiResponse<EmbedUsageResponse>>, BadRequest<ApiResponse<object>>>> HandleUsage(
        [FromServices] IEmbedAnalyticsStore analytics,
        [FromQuery] string? groupBy,
        [FromQuery] string? integrationId,
        [FromQuery] string? tenantId,
        [FromQuery] string? origin,
        [FromQuery] string? serviceId,
        [FromQuery] string? layerId,
        [FromQuery] string? eventType,
        HttpContext context)
    {
        if (!TryParseDimension(groupBy, out var dimension))
        {
            return TypedResults.BadRequest(ApiResponse<object>.Failure($"Unknown groupBy value '{groupBy}'"));
        }

        EmbedAnalyticsEventType? eventTypeFilter = null;
        if (!string.IsNullOrWhiteSpace(eventType))
        {
            if (!TryParseEventType(eventType, out var parsed))
            {
                return TypedResults.BadRequest(ApiResponse<object>.Failure($"Unknown eventType value '{eventType}'"));
            }

            eventTypeFilter = parsed;
        }

        var query = new EmbedUsageQuery
        {
            GroupBy = dimension,
            IntegrationId = integrationId,
            TenantId = tenantId,
            Origin = origin,
            ServiceId = serviceId,
            LayerId = layerId,
            EventType = eventTypeFilter,
        };

        var report = await analytics.QueryAsync(query, context.RequestAborted);
        var response = new EmbedUsageResponse
        {
            GroupBy = report.GroupBy.ToString(),
            Total = report.Total,
            Aggregates = report.Aggregates
                .Select(a => new EmbedUsageAggregateDto { Key = a.Key, Count = a.Count })
                .ToList()
                .AsReadOnly(),
        };

        return TypedResults.Ok(ApiResponse<EmbedUsageResponse>.CreateSuccess(response));
    }

    private static bool TryBuildScope(CreateEmbedKeyRequest request, out EmbedKeyScope scope, out string validationMessage)
    {
        scope = new EmbedKeyScope();

        var validationResults = new List<ValidationResult>();
        if (!Validator.TryValidateObject(request, new ValidationContext(request), validationResults, true))
        {
            validationMessage = $"Validation failed: {string.Join(", ", validationResults.Select(r => r.ErrorMessage))}";
            return false;
        }

        if (request.Scope is null)
        {
            validationMessage = "Validation failed: scope is required";
            return false;
        }

        var origins = Sanitize(request.Scope.AllowedEmbedOrigins);
        if (origins.Count == 0)
        {
            validationMessage = "Validation failed: at least one allowed embed origin is required";
            return false;
        }

        if (request.Scope.RateLimitRequestsPerWindow < 0)
        {
            validationMessage = "Validation failed: rateLimitRequestsPerWindow must be zero or greater";
            return false;
        }

        var windowSeconds = request.Scope.RateLimitWindowSeconds <= 0 ? 60 : request.Scope.RateLimitWindowSeconds;

        scope = new EmbedKeyScope
        {
            AllowedEmbedOrigins = origins,
            AllowedServiceOrigins = Sanitize(request.Scope.AllowedServiceOrigins),
            AllowedContentIds = Sanitize(request.Scope.AllowedContentIds),
            TenantId = Trimmed(request.Scope.TenantId),
            IntegrationId = Trimmed(request.Scope.IntegrationId),
            Edition = Trimmed(request.Scope.Edition),
            RateLimitRequestsPerWindow = request.Scope.RateLimitRequestsPerWindow,
            RateLimitWindow = TimeSpan.FromSeconds(windowSeconds),
        };

        validationMessage = string.Empty;
        return true;
    }

    private static EmbedKeyResponse ToResponse(EmbedKeyRecord record, DateTimeOffset now) => new()
    {
        Id = record.Id,
        Name = record.Name,
        KeyPrefix = record.KeyPrefix,
        Status = record.GetStatus(now).ToString().ToLowerInvariant(),
        Scope = new EmbedKeyScopeDto
        {
            AllowedEmbedOrigins = record.Scope.AllowedEmbedOrigins,
            AllowedServiceOrigins = record.Scope.AllowedServiceOrigins,
            AllowedContentIds = record.Scope.AllowedContentIds,
            TenantId = record.Scope.TenantId,
            IntegrationId = record.Scope.IntegrationId,
            Edition = record.Scope.Edition,
            RateLimitRequestsPerWindow = record.Scope.RateLimitRequestsPerWindow,
            RateLimitWindowSeconds = (int)Math.Max(1, record.Scope.RateLimitWindow.TotalSeconds),
        },
        CreatedAt = record.CreatedAt,
        UpdatedAt = record.UpdatedAt,
        ExpiresAt = record.ExpiresAt,
        LastUsedAt = record.LastUsedAt,
        RotatedAt = record.RotatedAt,
        RevokedAt = record.RevokedAt,
        CreatedBy = record.CreatedBy,
    };

    private static List<string> Sanitize(IReadOnlyList<string>? values)
    {
        if (values is null)
        {
            return [];
        }

        return values
            .Where(static v => !string.IsNullOrWhiteSpace(v))
            .Select(static v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool TryParseDimension(string? value, out EmbedUsageDimension dimension)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            dimension = EmbedUsageDimension.EventType;
            return true;
        }

        return Enum.TryParse(value, ignoreCase: true, out dimension);
    }

    private static bool TryParseEventType(string value, out EmbedAnalyticsEventType eventType) =>
        Enum.TryParse(value, ignoreCase: true, out eventType) && Enum.IsDefined(eventType);

    private static string ResolveActor(HttpContext context)
    {
        var apiKeyId = context.User.FindFirst("api_key_id")?.Value;
        if (!string.IsNullOrWhiteSpace(apiKeyId))
        {
            return $"api-key:{apiKeyId}";
        }

        return context.User.Identity?.Name ?? AuditEvent.AnonymousActor;
    }

    private static Task EmitAuditAsync(
        IAuditLog auditLog,
        HttpContext context,
        AuditEventType eventType,
        string action,
        string resourceId,
        AuditOutcome outcome,
        string actor)
    {
        var auditEvent = new AuditEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            EventType = eventType,
            Actor = actor,
            ActorType = actor.StartsWith("api-key:", StringComparison.Ordinal)
                ? AuditActorType.ApiKey
                : AuditActorType.UserId,
            ResourceType = "embed_key",
            ResourceId = resourceId,
            Action = action,
            Outcome = outcome,
            CorrelationId = context.TraceIdentifier,
        };

        return auditLog.RecordAsync(auditEvent, context.RequestAborted);
    }

    [LoggerMessage(EventId = 4600, Level = LogLevel.Information,
        Message = "Created embed key {EmbedKeyId} named '{Name}'")]
    private static partial void LogKeyCreated(ILogger logger, Guid embedKeyId, string name);

    [LoggerMessage(EventId = 4601, Level = LogLevel.Information,
        Message = "Rotated embed key {EmbedKeyId}")]
    private static partial void LogKeyRotated(ILogger logger, Guid embedKeyId);

    [LoggerMessage(EventId = 4602, Level = LogLevel.Information,
        Message = "Revoked embed key {EmbedKeyId}")]
    private static partial void LogKeyRevoked(ILogger logger, Guid embedKeyId);
}
