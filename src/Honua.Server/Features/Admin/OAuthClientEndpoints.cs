// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Operations;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

internal static partial class OAuthClientEndpoints
{
    // Tokens issued to a client carry no agent marker, so a client is registered by a person, never
    // on an agent's call or the replay of a proposal an agent made.
    private const string AgentRegistrationRefusal =
        "Validation failed: an OAuth client is registered by a person, not by an agent or from a proposal an agent made";

    internal sealed class OAuthClientEndpointsLog;

    public static void MapOAuthClientEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var clients = endpoints.MapGroup("/api/v{version:apiVersion}/admin/oauth-clients")
            .WithMetadata(TenantIndependentControlPlaneMetadata.Instance)
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "OAuth Clients")
            .RequireAdminAuthorization()
            .RequirePlatformAdminAuthority();

        var listClients = AdminHttpOperationRouteBinding.ForAccess("admin.oauth-client.list", "/oauth-clients");
        clients.MapMethods(listClients.RoutePattern, listClients.Methods, HandleListClients)
            .WithAdminOperation(listClients.Definition).WithDisplayName("List OAuth Clients");

        // Registering or deleting a client issues or removes a credential, so general admin
        // write is not enough.
        var registerClient = AdminHttpOperationRouteBinding.ForAccess("admin.oauth-client.register", "/oauth-clients");
        clients.MapMethods(registerClient.RoutePattern, registerClient.Methods, HandleCreateClient)
            .WithAdminOperation(registerClient.Definition).WithDisplayName("Register OAuth Client")
            .RequireAdminCredentialManagementAuthorization();

        var getClient = AdminHttpOperationRouteBinding.ForAccess("admin.oauth-client.get", "/oauth-clients", "/{id:guid}");
        clients.MapMethods(getClient.RoutePattern, getClient.Methods, HandleGetClient)
            .WithAdminOperation(getClient.Definition).WithDisplayName("Get OAuth Client");

        var deleteClient = AdminHttpOperationRouteBinding.ForAccess("admin.oauth-client.delete", "/oauth-clients", "/{id:guid}");
        clients.MapMethods(deleteClient.RoutePattern, deleteClient.Methods, HandleDeleteClient)
            .WithAdminOperation(deleteClient.Definition).WithDisplayName("Delete OAuth Client")
            .RequireAdminCredentialManagementAuthorization();

        MapOAuthScopeEndpoints(endpoints);
    }

    private static void MapOAuthScopeEndpoints(IEndpointRouteBuilder endpoints)
    {
        var scopes = endpoints.MapGroup("/api/v{version:apiVersion}/admin/oauth-scopes")
            .WithMetadata(TenantIndependentControlPlaneMetadata.Instance)
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "OAuth Scopes")
            .RequireAdminAuthorization()
            .RequirePlatformAdminAuthority();

        var listScopes = AdminHttpOperationRouteBinding.ForAccess("admin.oauth-scope.list", "/oauth-scopes");
        scopes.MapMethods(listScopes.RoutePattern, listScopes.Methods, HandleListScopes)
            .WithAdminOperation(listScopes.Definition).WithDisplayName("List OAuth Scopes");

        // A scope's permissions become the authority of every client token that requests it, so
        // redefining one is a credential change, like registering the client itself.
        var defineScope = AdminHttpOperationRouteBinding.ForAccess("admin.oauth-scope.define", "/oauth-scopes");
        scopes.MapMethods(defineScope.RoutePattern, defineScope.Methods, HandleDefineScope)
            .WithAdminOperation(defineScope.Definition).WithDisplayName("Define OAuth Scope")
            .AddAgentApprovalFilter(AgentApprovalFilter.AccessGrants)
            .RequireCredentialManagementAuthority();

        var deleteScope = AdminHttpOperationRouteBinding.ForAccess("admin.oauth-scope.delete", "/oauth-scopes");
        scopes.MapMethods(deleteScope.RoutePattern, deleteScope.Methods, HandleDeleteScope)
            .WithAdminOperation(deleteScope.Definition).WithDisplayName("Delete OAuth Scope")
            .AddAgentApprovalFilter(AgentApprovalFilter.AccessGrants)
            .RequireCredentialManagementAuthority();
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
        if (AdminApiKeyKind.CallerOf(context) == AdminApiKeyCaller.Agent)
        {
            return TypedResults.BadRequest(ApiResponse<object>.Failure(AgentRegistrationRefusal));
        }

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
            CreatedBy: AdminPrincipalHelpers.ResolveCreator(context));

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

        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Any(char.IsControl))
        {
            validationMessage = "Validation failed: name is required and must not contain control characters";
            return false;
        }

        var parsedClientType = ParseClientType(request.ClientType);
        if (parsedClientType is null)
        {
            validationMessage = "Validation failed: clientType must be 'confidential' or 'public'";
            return false;
        }

        clientType = parsedClientType.Value;

        if (request.AllowedGrantTypes is not null && request.AllowedGrantTypes.Any(string.IsNullOrWhiteSpace))
        {
            validationMessage = "Validation failed: grant type labels are required";
            return false;
        }

        if (request.RedirectUris is not null && request.RedirectUris.Any(uri => !IsValidRedirectUri(uri)))
        {
            validationMessage = "Validation failed: redirectUris must be absolute HTTP(S) URIs without control characters or fragments";
            return false;
        }

        if (request.AllowedScopes is not null && request.AllowedScopes.Any(scope => !IsValidScopeToken(scope)))
        {
            validationMessage = "Validation failed: scope labels must be valid RFC 6749 scope tokens";
            return false;
        }

        validationMessage = string.Empty;
        return true;
    }

    private static OAuthClientType? ParseClientType(string? rawClientType)
    {
        var rawType = rawClientType?.Trim();
        if (string.IsNullOrWhiteSpace(rawType) ||
            string.Equals(rawType, "confidential", StringComparison.OrdinalIgnoreCase))
        {
            return OAuthClientType.Confidential;
        }

        if (string.Equals(rawType, "public", StringComparison.OrdinalIgnoreCase))
        {
            return OAuthClientType.Public;
        }

        return null;
    }

    private static bool IsValidRedirectUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
        {
            return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            && string.IsNullOrEmpty(uri.Fragment);
    }

    private static bool IsValidScopeToken(string? scope)
    {
        if (string.IsNullOrEmpty(scope))
        {
            return false;
        }

        foreach (var ch in scope)
        {
            if (ch < 0x21 || ch > 0x7E || ch is '"' or '\\' or '/' or '<' or '>')
            {
                return false;
            }
        }

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

        if (!IsValidScopeToken(request.Scope))
        {
            validationMessage = "Validation failed: scope must be a valid RFC 6749 scope token without whitespace, control characters, angle brackets, or path separators";
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
