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
/// Admin endpoints for the first-class OAuth2 client registry and scope catalogue
/// (ADR-0053 Increment 2, #1888). Distinct from the human API-key surface: these
/// manage per-application <c>client_id</c>/<c>client_secret</c> identities and the
/// scope→permission mapping that <c>client_credentials</c> tokens are minted from.
/// </summary>
internal static partial class OAuthClientEndpoints
{
    internal sealed class OAuthClientEndpointsLog;

    /// <summary>Registers OAuth2 client-registry and scope-catalogue admin endpoints.</summary>
    public static void MapOAuthClientEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var clients = endpoints.MapGroup("/api/v{version:apiVersion}/admin/oauth-clients")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "OAuth Clients")
            .RequireAdminAuthorization();

        clients.MapGet("/", HandleListClients)
            .WithDisplayName("List OAuth Clients")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        clients.MapPost("/", HandleCreateClient)
            .WithDisplayName("Register OAuth Client")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        clients.MapGet("/{id:guid}", HandleGetClient)
            .WithDisplayName("Get OAuth Client")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        clients.MapDelete("/{id:guid}", HandleDeleteClient)
            .WithDisplayName("Delete OAuth Client")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Delete }));

        var scopes = endpoints.MapGroup("/api/v{version:apiVersion}/admin/oauth-scopes")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "OAuth Scopes")
            .RequireAdminAuthorization();

        scopes.MapGet("/", HandleListScopes)
            .WithDisplayName("List OAuth Scopes")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        scopes.MapPut("/", HandleDefineScope)
            .WithDisplayName("Define OAuth Scope")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Put }));

        scopes.MapDelete("/{scope}", HandleDeleteScope)
            .WithDisplayName("Delete OAuth Scope")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Delete }));
    }

    private static async Task<Ok<ApiResponse<IReadOnlyList<OAuthClientResponse>>>> HandleListClients(
        [FromServices] IOAuthClientStore store,
        HttpContext context)
    {
        var records = await store.ListAsync(context.RequestAborted);
        var response = records.Select(ToResponse).ToList().AsReadOnly();
        return TypedResults.Ok(ApiResponse<IReadOnlyList<OAuthClientResponse>>.CreateSuccess(response));
    }

    private static async Task<Results<Created<ApiResponse<OAuthClientSecretResponse>>, BadRequest<ApiResponse<object>>>>
        HandleCreateClient(
            CreateOAuthClientRequest request,
            [FromServices] IOAuthClientStore store,
            [FromServices] ILogger<OAuthClientEndpointsLog> logger,
            HttpContext context)
    {
        if (!TryValidateCreateRequest(request, out var clientType, out var validationMessage))
        {
            return TypedResults.BadRequest(ApiResponse<object>.Failure(validationMessage));
        }

        var registration = new OAuthClientRegistration(
            Name: request.Name.Trim(),
            ClientType: clientType,
            AllowedGrantTypes: request.AllowedGrantTypes ?? [],
            RedirectUris: request.RedirectUris ?? [],
            AllowedScopes: request.AllowedScopes ?? [],
            ExpiresAt: request.ExpiresAt,
            CreatedBy: ResolveCreator(context));

        var result = await store.CreateAsync(registration, context.RequestAborted);
        LogClientRegistered(logger, result.Record.Id, result.Record.ClientId);

        var response = new OAuthClientSecretResponse
        {
            Client = ToResponse(result.Record),
            ClientSecret = result.Secret,
        };

        return TypedResults.Created(
            $"/api/v1/admin/oauth-clients/{result.Record.Id}",
            ApiResponse<OAuthClientSecretResponse>.CreateSuccess(response));
    }

    private static async Task<Results<Ok<ApiResponse<OAuthClientResponse>>, NotFound<ApiResponse<object>>>> HandleGetClient(
        Guid id,
        [FromServices] IOAuthClientStore store,
        HttpContext context)
    {
        var record = await store.GetAsync(id, context.RequestAborted);
        if (record is null)
        {
            return TypedResults.NotFound(ApiResponse<object>.Failure("OAuth client not found"));
        }

        return TypedResults.Ok(ApiResponse<OAuthClientResponse>.CreateSuccess(ToResponse(record)));
    }

    private static async Task<Results<Ok<ApiResponse<OAuthClientResponse>>, NotFound<ApiResponse<object>>>> HandleDeleteClient(
        Guid id,
        [FromServices] IOAuthClientStore store,
        [FromServices] ILogger<OAuthClientEndpointsLog> logger,
        HttpContext context)
    {
        var record = await store.DeleteAsync(id, context.RequestAborted);
        if (record is null)
        {
            return TypedResults.NotFound(ApiResponse<object>.Failure("OAuth client not found"));
        }

        LogClientDeleted(logger, id);
        return TypedResults.Ok(ApiResponse<OAuthClientResponse>.CreateSuccess(ToResponse(record)));
    }

    private static async Task<Ok<ApiResponse<IReadOnlyList<OAuthScopeResponse>>>> HandleListScopes(
        [FromServices] IOAuthScopeCatalogue catalogue,
        HttpContext context)
    {
        var scopes = await catalogue.ListAsync(context.RequestAborted);
        var response = scopes.Select(ToResponse).ToList().AsReadOnly();
        return TypedResults.Ok(ApiResponse<IReadOnlyList<OAuthScopeResponse>>.CreateSuccess(response));
    }

    private static async Task<Results<Ok<ApiResponse<OAuthScopeResponse>>, BadRequest<ApiResponse<object>>>> HandleDefineScope(
        DefineOAuthScopeRequest request,
        [FromServices] IOAuthScopeCatalogue catalogue,
        [FromServices] ILogger<OAuthClientEndpointsLog> logger,
        HttpContext context)
    {
        if (!TryValidateDefineScopeRequest(request, out var validationMessage))
        {
            return TypedResults.BadRequest(ApiResponse<object>.Failure(validationMessage));
        }

        var defined = await catalogue.DefineAsync(
            new OAuthScopeDefinition(
                Scope: request.Scope.Trim(),
                Description: request.Description ?? string.Empty,
                Permissions: request.Permissions ?? []),
            context.RequestAborted);

        LogScopeDefined(logger, defined.Scope);
        return TypedResults.Ok(ApiResponse<OAuthScopeResponse>.CreateSuccess(ToResponse(defined)));
    }

    private static async Task<Results<Ok<ApiResponse<OAuthScopeResponse>>, NotFound<ApiResponse<object>>>> HandleDeleteScope(
        string scope,
        [FromServices] IOAuthScopeCatalogue catalogue,
        [FromServices] ILogger<OAuthClientEndpointsLog> logger,
        HttpContext context)
    {
        var removed = await catalogue.DeleteAsync(scope, context.RequestAborted);
        if (removed is null)
        {
            return TypedResults.NotFound(ApiResponse<object>.Failure("OAuth scope not found"));
        }

        LogScopeDeleted(logger, removed.Scope);
        return TypedResults.Ok(ApiResponse<OAuthScopeResponse>.CreateSuccess(ToResponse(removed)));
    }

    private static bool TryValidateCreateRequest(
        CreateOAuthClientRequest request,
        out OAuthClientType clientType,
        out string validationMessage)
    {
        clientType = OAuthClientType.Confidential;

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

        var rawType = request.ClientType?.Trim();
        if (string.IsNullOrWhiteSpace(rawType) ||
            string.Equals(rawType, "confidential", StringComparison.OrdinalIgnoreCase))
        {
            clientType = OAuthClientType.Confidential;
        }
        else if (string.Equals(rawType, "public", StringComparison.OrdinalIgnoreCase))
        {
            clientType = OAuthClientType.Public;
        }
        else
        {
            validationMessage = "Validation failed: clientType must be 'confidential' or 'public'";
            return false;
        }

        if (request.AllowedGrantTypes is not null && request.AllowedGrantTypes.Any(string.IsNullOrWhiteSpace))
        {
            validationMessage = "Validation failed: grant type labels are required";
            return false;
        }

        if (request.AllowedScopes is not null && request.AllowedScopes.Any(string.IsNullOrWhiteSpace))
        {
            validationMessage = "Validation failed: scope labels are required";
            return false;
        }

        validationMessage = string.Empty;
        return true;
    }

    private static bool TryValidateDefineScopeRequest(DefineOAuthScopeRequest request, out string validationMessage)
    {
        var validationResults = new List<ValidationResult>();
        if (!Validator.TryValidateObject(request, new ValidationContext(request), validationResults, true))
        {
            validationMessage = $"Validation failed: {string.Join(", ", validationResults.Select(r => r.ErrorMessage))}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.Scope))
        {
            validationMessage = "Validation failed: scope is required";
            return false;
        }

        if (request.Permissions is not null && request.Permissions.Any(string.IsNullOrWhiteSpace))
        {
            validationMessage = "Validation failed: permission labels are required";
            return false;
        }

        validationMessage = string.Empty;
        return true;
    }

    private static OAuthClientResponse ToResponse(OAuthClientRecord record) => new()
    {
        Id = record.Id,
        ClientId = record.ClientId,
        ClientType = record.ClientType == OAuthClientType.Public ? "public" : "confidential",
        Name = record.Name,
        SecretPrefix = record.SecretPrefix,
        AllowedGrantTypes = record.AllowedGrantTypes,
        RedirectUris = record.RedirectUris,
        AllowedScopes = record.AllowedScopes,
        Status = GetStatus(record),
        CreatedAt = record.CreatedAt,
        UpdatedAt = record.UpdatedAt,
        ExpiresAt = record.ExpiresAt,
        LastUsedAt = record.LastUsedAt,
        CreatedBy = record.CreatedBy,
    };

    private static OAuthScopeResponse ToResponse(OAuthScopeDefinition definition) => new()
    {
        Scope = definition.Scope,
        Description = definition.Description,
        Permissions = definition.Permissions,
    };

    private static string GetStatus(OAuthClientRecord record)
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

    [LoggerMessage(EventId = 4570, Level = LogLevel.Information,
        Message = "Registered OAuth client {RegistrationId} with client_id '{ClientId}'")]
    private static partial void LogClientRegistered(ILogger logger, Guid registrationId, string clientId);

    [LoggerMessage(EventId = 4571, Level = LogLevel.Information,
        Message = "Deleted OAuth client {RegistrationId}")]
    private static partial void LogClientDeleted(ILogger logger, Guid registrationId);

    [LoggerMessage(EventId = 4572, Level = LogLevel.Information,
        Message = "Defined OAuth scope '{Scope}'")]
    private static partial void LogScopeDefined(ILogger logger, string scope);

    [LoggerMessage(EventId = 4573, Level = LogLevel.Information,
        Message = "Deleted OAuth scope '{Scope}'")]
    private static partial void LogScopeDeleted(ILogger logger, string scope);
}
