// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using Honua.Core.Features.AuditLog;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Operations;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

internal static partial class AdminApiKeyEndpoints
{
    internal sealed class AdminApiKeyEndpointsLog;

    public static void MapAdminApiKeyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/api-keys")
            .WithMetadata(TenantIndependentControlPlaneMetadata.Instance)
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "API Keys")
            .RequireAdminAuthorization()
            .RequirePlatformAdminAuthority();

        var list = AdminHttpOperationRouteBinding.ForAccess("admin.api-key.list", "/api-keys");
        group.MapMethods(list.RoutePattern, list.Methods, HandleListApiKeys)
            .WithAdminOperation(list.Definition).WithDisplayName("List Admin API Keys");

        // Issuing, rotating, revoking or replacing restrictions needs full-admin credential
        // authority; general admin write (admin:write, admin:manage) keeps the read routes only.
        var create = AdminHttpOperationRouteBinding.ForAccess("admin.api-key.create", "/api-keys");
        group.MapMethods(create.RoutePattern, create.Methods, HandleCreateApiKey)
            .WithAdminOperation(create.Definition).WithDisplayName("Create Admin API Key")
            .RequireAdminCredentialManagementAuthorization();

        var rotate = AdminHttpOperationRouteBinding.ForAccess("admin.api-key.rotate", "/api-keys", "/{id:guid}/rotate");
        group.MapMethods(rotate.RoutePattern, rotate.Methods, HandleRotateApiKey)
            .WithAdminOperation(rotate.Definition).WithDisplayName("Rotate Admin API Key")
            .RequireAdminCredentialManagementAuthorization();

        var revoke = AdminHttpOperationRouteBinding.ForAccess("admin.api-key.revoke", "/api-keys", "/{id:guid}/revoke");
        group.MapMethods(revoke.RoutePattern, revoke.Methods, HandleRevokeApiKey)
            .WithAdminOperation(revoke.Definition).WithDisplayName("Revoke Admin API Key")
            .RequireAdminCredentialManagementAuthorization();

        var restrictions = AdminHttpOperationRouteBinding.ForAccess("admin.api-key.update-restrictions", "/api-keys", "/{id:guid}/restrictions");
        group.MapMethods(restrictions.RoutePattern, restrictions.Methods, HandleUpdateApiKeyRestrictions)
            .WithAdminOperation(restrictions.Definition).WithDisplayName("Update Admin API Key Restrictions")
            .RequireAdminCredentialManagementAuthorization();

        var effective = AdminHttpOperationRouteBinding.ForAccess("admin.api-key.effective-permissions", "/api-keys", "/{id:guid}/effective-permissions");
        group.MapMethods(effective.RoutePattern, effective.Methods, HandleGetEffectivePermissions)
            .WithAdminOperation(effective.Definition).WithDisplayName("Get Admin API Key Effective Permissions");
    }

    private static async Task<Ok<ApiResponse<IReadOnlyList<AdminApiKeyResponse>>>> HandleListApiKeys(
        [FromServices] IAdminApiKeyStore store,
        HttpContext context)
    {
        var records = await store.ListAsync(context.RequestAborted);
        var response = records.Where(record => !record.Permissions.Any(AdminApiKeyPermission.IsApprovedOperationGrant))
            .Select(ToResponse).ToList().AsReadOnly();
        return TypedResults.Ok(ApiResponse<IReadOnlyList<AdminApiKeyResponse>>.CreateSuccess(response));
    }

    private static async Task<Results<Created<ApiResponse<AdminApiKeySecretResponse>>, BadRequest<ApiResponse<object>>>>
        HandleCreateApiKey(
            CreateAdminApiKeyRequest request,
            [FromServices] IAdminApiKeyStore store,
            [FromServices] ILogger<AdminApiKeyEndpointsLog> logger,
            HttpContext context)
    {
        if (!TryValidateCreateRequest(request, out var requested, out var validationMessage) ||
            !TryValidateRestrictionLists(request.AllowedReferrers, request.AllowedIpRanges, out var restrictions, out validationMessage))
        {
            return TypedResults.BadRequest(ApiResponse<object>.Failure(validationMessage));
        }

        var kind = ResolveKind(request, requested, context);
        if (kind.Refusal is { } refusal)
        {
            return TypedResults.BadRequest(ApiResponse<object>.Failure(refusal));
        }

        var result = await store.CreateAsync(
            request.Name.Trim(),
            kind.Permissions,
            request.ExpiresAt,
            AdminPrincipalHelpers.ResolveCreator(context),
            restrictions,
            context.RequestAborted);

        if (IsSupportSessionKey(kind.Permissions))
        {
            var audited = await TryAuditSupportSessionKeyAsync(
                context,
                "ops.support_session.granted",
                result.Record,
                context.RequestAborted).ConfigureAwait(false);
            if (!audited)
            {
                await store.RevokeAsync(result.Record.Id, context.RequestAborted).ConfigureAwait(false);
                throw new InvalidOperationException("Support session grant aborted: tamper-evident audit log write failed.");
            }
        }

        LogApiKeyCreated(logger, result.Record.Id, result.Record.Name);
        var response = new AdminApiKeySecretResponse
        {
            ApiKey = ToResponse(result.Record),
            Key = result.Key,
        };

        return TypedResults.Created(
            $"/api/v1/admin/api-keys/{result.Record.Id}",
            ApiResponse<AdminApiKeySecretResponse>.CreateSuccess(response));
    }

    // Rotation returns the new secret to the caller, so an agent caller may rotate only an agent key.
    private static async Task<Results<Ok<ApiResponse<AdminApiKeySecretResponse>>, NotFound<ApiResponse<object>>, BadRequest<ApiResponse<object>>>> HandleRotateApiKey(
        Guid id,
        [FromServices] IAdminApiKeyStore store,
        [FromServices] ILogger<AdminApiKeyEndpointsLog> logger,
        HttpContext context)
    {
        var caller = AdminApiKeyKind.CallerOf(context);
        if (caller == AdminApiKeyCaller.Agent
            && await store.GetAsync(id, context.RequestAborted) is { } current
            && !AdminApiKeyKind.MayRotate(caller, current.Permissions))
        {
            return TypedResults.BadRequest(ApiResponse<object>.Failure(
                "Validation failed: an agent, or a proposal an agent made, may rotate only agent keys"));
        }

        var result = await store.RotateAsync(id, context.RequestAborted);
        if (result is null)
        {
            return TypedResults.NotFound(ApiResponse<object>.Failure("API key not found, expired, or revoked"));
        }

        LogApiKeyRotated(logger, id);
        var response = new AdminApiKeySecretResponse
        {
            ApiKey = ToResponse(result.Record),
            Key = result.Key,
        };

        return TypedResults.Ok(ApiResponse<AdminApiKeySecretResponse>.CreateSuccess(response));
    }

    private static async Task<Results<Ok<ApiResponse<AdminApiKeyResponse>>, NotFound<ApiResponse<object>>>> HandleRevokeApiKey(
        Guid id,
        [FromServices] IAdminApiKeyStore store,
        [FromServices] ILogger<AdminApiKeyEndpointsLog> logger,
        HttpContext context)
    {
        var record = await store.RevokeAsync(id, context.RequestAborted);
        if (record is null)
        {
            return TypedResults.NotFound(ApiResponse<object>.Failure("API key not found"));
        }

        if (IsSupportSessionKey(record.Permissions))
        {
            var audited = await TryAuditSupportSessionKeyAsync(
                context,
                "ops.support_session.revoked",
                record,
                context.RequestAborted).ConfigureAwait(false);
            if (!audited)
            {
                throw new InvalidOperationException("Support session revocation audit failed: tamper-evident audit log write failed.");
            }
        }

        LogApiKeyRevoked(logger, id);
        return TypedResults.Ok(ApiResponse<AdminApiKeyResponse>.CreateSuccess(ToResponse(record)));
    }

    private static async Task<Results<Ok<ApiResponse<AdminApiKeyResponse>>, NotFound<ApiResponse<object>>, BadRequest<ApiResponse<object>>>>
        HandleUpdateApiKeyRestrictions(
            Guid id,
            UpdateAdminApiKeyRestrictionsRequest? request,
            [FromServices] IAdminApiKeyStore store,
            [FromServices] ILogger<AdminApiKeyEndpointsLog> logger,
            HttpContext context)
    {
        if (request is null)
        {
            return TypedResults.BadRequest(ApiResponse<object>.Failure("Validation failed: restrictions body is required"));
        }

        if (!TryValidateRestrictionLists(
                request.AllowedReferrers,
                request.AllowedIpRanges,
                out var restrictions,
                out var validationMessage))
        {
            return TypedResults.BadRequest(ApiResponse<object>.Failure(validationMessage));
        }

        // An omitted or empty list clears that restriction. The replacement is the whole set.
        var record = await store.UpdateRestrictionsAsync(
            id,
            restrictions ?? new AdminApiKeyRestrictions(null, null),
            context.RequestAborted);
        if (record is null)
        {
            return TypedResults.NotFound(ApiResponse<object>.Failure("API key not found"));
        }

        LogApiKeyRestrictionsUpdated(
            logger,
            id,
            record.AllowedReferrers is { Count: > 0 },
            record.AllowedIpRanges is { Count: > 0 });

        return TypedResults.Ok(ApiResponse<AdminApiKeyResponse>.CreateSuccess(ToResponse(record)));
    }

    private static async Task<Results<Ok<ApiResponse<AdminApiKeyEffectivePermissionsResponse>>, NotFound<ApiResponse<object>>>>
        HandleGetEffectivePermissions(
            Guid id,
            [FromServices] IAdminApiKeyStore store,
            HttpContext context)
    {
        var record = await store.GetAsync(id, context.RequestAborted);
        if (record is null || record.Permissions.Any(AdminApiKeyPermission.IsApprovedOperationGrant))
        {
            return TypedResults.NotFound(ApiResponse<object>.Failure("API key not found"));
        }

        var status = GetStatus(record);
        var response = new AdminApiKeyEffectivePermissionsResponse
        {
            Id = record.Id,
            Name = record.Name,
            Status = status,
            Kind = AdminApiKeyKind.Of(record.Permissions),
            Permissions = record.Permissions,
            CanAuthenticate = string.Equals(status, "active", StringComparison.Ordinal),
        };

        return TypedResults.Ok(ApiResponse<AdminApiKeyEffectivePermissionsResponse>.CreateSuccess(response));
    }

    private static bool IsSupportSessionKey(IReadOnlyList<string> permissions)
        => permissions.Any(static permission =>
            string.Equals(permission?.Trim(), AdminApiKeyPermission.OpsPropose, StringComparison.OrdinalIgnoreCase)
            || string.Equals(permission?.Trim(), AdminApiKeyPermission.OpsRead, StringComparison.OrdinalIgnoreCase));

    private static async Task<bool> TryAuditSupportSessionKeyAsync(
        HttpContext context,
        string action,
        AdminApiKeyRecord record,
        CancellationToken cancellationToken)
    {
        var auditLog = context.RequestServices.GetService<IAuditLog>();
        if (auditLog is null || auditLog is NullAuditLog || !auditLog.IsPersisted)
        {
            return true;
        }

        var actor = AdminPrincipalHelpers.ResolveCreator(context) ?? AuditEvent.AnonymousActor;
        var permissionsList = string.Join(",", record.Permissions);
        var escapedName = record.Name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        var escapedPerms = permissionsList.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        var expiresAt = record.ExpiresAt?.ToString("O") ?? "none";
        var details = $"{{\"keyId\":\"{record.Id:D}\",\"name\":\"{escapedName}\",\"permissions\":\"{escapedPerms}\",\"expiresAt\":\"{expiresAt}\"}}";
        var auditId = await auditLog.RecordAsync(
            new AuditEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                EventType = AuditEventType.AdminAction,
                Actor = actor,
                ActorType = actor == AuditEvent.AnonymousActor ? AuditActorType.Anonymous : AuditActorType.UserId,
                ResourceType = "ops_support_session",
                ResourceId = record.Id.ToString("D"),
                Action = action,
                Outcome = AuditOutcome.Success,
                CorrelationId = context.TraceIdentifier ?? record.Id.ToString("D"),
                Details = details,
            },
            cancellationToken).ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(auditId);
    }

    private static AdminApiKeyKindResult ResolveKind(
        CreateAdminApiKeyRequest request, IReadOnlyList<string> requested, HttpContext context)
        => AdminApiKeyKind.Resolve(new AdminApiKeyKindRequest
        {
            Kind = request.Kind,
            Permissions = requested,
            Caller = AdminApiKeyKind.CallerOf(context),
        });

    private static bool TryValidateCreateRequest(
        CreateAdminApiKeyRequest request,
        out IReadOnlyList<string> permissions,
        out string validationMessage)
    {
        permissions = [];

        var validationResults = new List<ValidationResult>();
        if (!Validator.TryValidateObject(request, new ValidationContext(request), validationResults, true))
        {
            validationMessage = $"Validation failed: {string.Join(", ", validationResults.Select(r => r.ErrorMessage))}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            validationMessage = "Validation failed: name is required";
            return false;
        }

        if (request.Permissions is null)
        {
            validationMessage = "Validation failed: permissions are required";
            return false;
        }

        if (request.Permissions.Any(string.IsNullOrWhiteSpace))
        {
            validationMessage = "Validation failed: permission labels are required";
            return false;
        }

        if (request.Permissions.Any(static permission => permission.Length > 200))
        {
            validationMessage = "Validation failed: permission labels must be 200 characters or fewer";
            return false;
        }

        if (request.Permissions.Any(AdminApiKeyPermission.IsApprovedOperationGrant))
        {
            validationMessage = "Validation failed: approved-operation grants are minted internally";
            return false;
        }

        // Resource grants must name a target. Reject a bare "write:"/"read:" (or
        // "{prefix}{service}/" / "{prefix}/{layer}") so an operator cannot mint a key
        // that authenticates but matches no resource. Only read grants accept the
        // "read:*" and "read:{service}/*" wildcards; write grants never do.
        if (request.Permissions.Any(static permission => LayerScopedWriteKey.IsMalformedResourceGrant(permission)))
        {
            validationMessage =
                "Validation failed: resource grants must be 'write:{service}', 'write:{service}/{layer}', " +
                "'read:{service}', 'read:{service}/{layer}', 'read:{service}/*', 'read:*' or " +
                "'execute:{service}/{canonical-process-id}' (no execution wildcards)";
            return false;
        }

        permissions = request.Permissions;
        validationMessage = string.Empty;
        return true;
    }

    private static bool TryValidateRestrictionLists(
        IReadOnlyList<string>? allowedReferrers,
        IReadOnlyList<string>? allowedIpRanges,
        out AdminApiKeyRestrictions? restrictions,
        out string validationMessage)
    {
        restrictions = null;
        if (!TryNormalizeList(allowedReferrers, "allowedReferrers",
                "an origin such as 'https://app.example.com' or 'https://*.example.com' (no path)",
                ApiKeyRequestRestrictions.TryNormalizeReferrerPattern, out var referrers, out validationMessage) ||
            !TryNormalizeList(allowedIpRanges, "allowedIpRanges",
                "a CIDR range such as '203.0.113.0/24' or '2001:db8::/32', or a single IP address",
                ApiKeyRequestRestrictions.TryNormalizeIpRange, out var ranges, out validationMessage))
        {
            return false;
        }

        if (referrers is not null || ranges is not null)
        {
            restrictions = new AdminApiKeyRestrictions(referrers, ranges);
        }

        return true;
    }

    private delegate bool Normalizer(string? value, out string normalized);

    private static bool TryNormalizeList(
        IReadOnlyList<string>? values,
        string field,
        string expected,
        Normalizer normalize,
        out IReadOnlyList<string>? normalized,
        out string validationMessage)
    {
        normalized = null;
        validationMessage = string.Empty;
        if (values is not { Count: > 0 })
        {
            return true;
        }

        if (values.Count > ApiKeyRequestRestrictions.MaxEntries)
        {
            validationMessage = $"Validation failed: {field} accepts at most {ApiKeyRequestRestrictions.MaxEntries} entries";
            return false;
        }

        var result = new List<string>(values.Count);
        foreach (var value in values)
        {
            if (!normalize(value, out var entry))
            {
                validationMessage = $"Validation failed: each {field} entry must be {expected}";
                return false;
            }

            if (!result.Contains(entry, StringComparer.Ordinal))
            {
                result.Add(entry);
            }
        }

        normalized = result;
        return true;
    }

    private static AdminApiKeyResponse ToResponse(AdminApiKeyRecord record) => new()
    {
        Id = record.Id,
        Name = record.Name,
        KeyPrefix = record.KeyPrefix,
        Kind = AdminApiKeyKind.Of(record.Permissions),
        Permissions = record.Permissions,
        Status = GetStatus(record),
        CreatedAt = record.CreatedAt,
        UpdatedAt = record.UpdatedAt,
        ExpiresAt = record.ExpiresAt,
        LastUsedAt = record.LastUsedAt,
        RotatedAt = record.RotatedAt,
        RevokedAt = record.RevokedAt,
        CreatedBy = record.CreatedBy,
        AllowedReferrers = record.AllowedReferrers,
        AllowedIpRanges = record.AllowedIpRanges,
    };

    private static string GetStatus(AdminApiKeyRecord record)
    {
        if (record.RevokedAt is not null)
        {
            return "revoked";
        }

        if (record.ExpiresAt.HasValue && record.ExpiresAt.Value <= DateTimeOffset.UtcNow)
        {
            return "expired";
        }

        return "active";
    }

    [LoggerMessage(EventId = 4560, Level = LogLevel.Information,
        Message = "Created admin API key {ApiKeyId} named '{Name}'")]
    private static partial void LogApiKeyCreated(ILogger logger, Guid apiKeyId, string name);

    [LoggerMessage(EventId = 4561, Level = LogLevel.Information,
        Message = "Rotated admin API key {ApiKeyId}")]
    private static partial void LogApiKeyRotated(ILogger logger, Guid apiKeyId);

    [LoggerMessage(EventId = 4562, Level = LogLevel.Information,
        Message = "Revoked admin API key {ApiKeyId}")]
    private static partial void LogApiKeyRevoked(ILogger logger, Guid apiKeyId);

    // Kinds only: whether each list is set. Patterns and key material stay out of the log.
    [LoggerMessage(EventId = 4563, Level = LogLevel.Information,
        Message = "Updated admin API key {ApiKeyId} restrictions (referrer={HasReferrer}, ip={HasIp})")]
    private static partial void LogApiKeyRestrictionsUpdated(ILogger logger, Guid apiKeyId, bool hasReferrer, bool hasIp);
}
