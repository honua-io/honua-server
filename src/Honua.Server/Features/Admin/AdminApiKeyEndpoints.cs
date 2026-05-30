// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using Honua.Server.Features.Admin.Models;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for server-managed API key lifecycle operations.
/// </summary>
internal static partial class AdminApiKeyEndpoints
{
    internal sealed class AdminApiKeyEndpointsLog;

    /// <summary>
    /// Registers admin API key lifecycle endpoints.
    /// </summary>
    public static void MapAdminApiKeyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/api-keys")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "API Keys")
            .RequireAdminAuthorization();

        group.MapGet("/", HandleListApiKeys)
            .WithDisplayName("List Admin API Keys")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPost("/", HandleCreateApiKey)
            .WithDisplayName("Create Admin API Key")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapPost("/{id:guid}/rotate", HandleRotateApiKey)
            .WithDisplayName("Rotate Admin API Key")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapPost("/{id:guid}/revoke", HandleRevokeApiKey)
            .WithDisplayName("Revoke Admin API Key")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapGet("/{id:guid}/effective-permissions", HandleGetEffectivePermissions)
            .WithDisplayName("Get Admin API Key Effective Permissions")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
    }

    private static async Task<Ok<ApiResponse<IReadOnlyList<AdminApiKeyResponse>>>> HandleListApiKeys(
        [FromServices] IAdminApiKeyStore store,
        HttpContext context)
    {
        var records = await store.ListAsync(context.RequestAborted);
        var response = records.Select(ToResponse).ToList().AsReadOnly();
        return TypedResults.Ok(ApiResponse<IReadOnlyList<AdminApiKeyResponse>>.CreateSuccess(response));
    }

    private static async Task<Results<Created<ApiResponse<AdminApiKeySecretResponse>>, BadRequest<ApiResponse<object>>>>
        HandleCreateApiKey(
            CreateAdminApiKeyRequest request,
            [FromServices] IAdminApiKeyStore store,
            [FromServices] ILogger<AdminApiKeyEndpointsLog> logger,
            HttpContext context)
    {
        if (!TryValidateCreateRequest(request, out var permissions, out var validationMessage))
        {
            return TypedResults.BadRequest(ApiResponse<object>.Failure(validationMessage));
        }

        var result = await store.CreateAsync(
            request.Name.Trim(),
            permissions,
            request.ExpiresAt,
            ResolveCreator(context),
            context.RequestAborted);

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

    private static async Task<Results<Ok<ApiResponse<AdminApiKeySecretResponse>>, NotFound<ApiResponse<object>>>> HandleRotateApiKey(
        Guid id,
        [FromServices] IAdminApiKeyStore store,
        [FromServices] ILogger<AdminApiKeyEndpointsLog> logger,
        HttpContext context)
    {
        var result = await store.RotateAsync(id, context.RequestAborted);
        if (result is null)
        {
            return TypedResults.NotFound(ApiResponse<object>.Failure("API key not found or revoked"));
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

        LogApiKeyRevoked(logger, id);
        return TypedResults.Ok(ApiResponse<AdminApiKeyResponse>.CreateSuccess(ToResponse(record)));
    }

    private static async Task<Results<Ok<ApiResponse<AdminApiKeyEffectivePermissionsResponse>>, NotFound<ApiResponse<object>>>>
        HandleGetEffectivePermissions(
            Guid id,
            [FromServices] IAdminApiKeyStore store,
            HttpContext context)
    {
        var record = await store.GetAsync(id, context.RequestAborted);
        if (record is null)
        {
            return TypedResults.NotFound(ApiResponse<object>.Failure("API key not found"));
        }

        var status = GetStatus(record);
        var response = new AdminApiKeyEffectivePermissionsResponse
        {
            Id = record.Id,
            Name = record.Name,
            Status = status,
            Permissions = record.Permissions,
            CanAuthenticate = string.Equals(status, "active", StringComparison.Ordinal),
        };

        return TypedResults.Ok(ApiResponse<AdminApiKeyEffectivePermissionsResponse>.CreateSuccess(response));
    }

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

        permissions = request.Permissions;
        validationMessage = string.Empty;
        return true;
    }

    private static AdminApiKeyResponse ToResponse(AdminApiKeyRecord record) => new()
    {
        Id = record.Id,
        Name = record.Name,
        KeyPrefix = record.KeyPrefix,
        Permissions = record.Permissions,
        Status = GetStatus(record),
        CreatedAt = record.CreatedAt,
        UpdatedAt = record.UpdatedAt,
        ExpiresAt = record.ExpiresAt,
        LastUsedAt = record.LastUsedAt,
        RotatedAt = record.RotatedAt,
        RevokedAt = record.RevokedAt,
        CreatedBy = record.CreatedBy,
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

    private static string? ResolveCreator(HttpContext context)
    {
        var apiKeyId = context.User.FindFirst("api_key_id")?.Value;
        if (!string.IsNullOrWhiteSpace(apiKeyId))
        {
            return $"api-key:{apiKeyId}";
        }

        return context.User.Identity?.Name;
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
}
