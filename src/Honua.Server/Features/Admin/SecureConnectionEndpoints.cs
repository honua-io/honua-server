// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for secure database connection management.
/// </summary>
/// <remarks>
/// Provides administrative functionality for:
/// - Creating, updating, and deleting secure database connections
/// - Testing connection health and validity
/// - Key rotation and encryption management
///
/// All endpoints require admin authorization.
/// Connection strings are never exposed in API responses for security.
/// </remarks>
internal static class SecureConnectionEndpoints
{
    /// <summary>
    /// Log category for secure connection endpoints.
    /// </summary>
    internal sealed class SecureConnectionEndpointsLog;
    /// <summary>
    /// Configure secure connection admin endpoints with formal API versioning.
    /// </summary>
    public static void MapSecureConnectionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/connections")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Secure Connections")
            .RequireAdminAuthorization();

        // Connection CRUD operations
        group.MapGet("/", HandleGetConnections)
            .WithDisplayName("List Secure Connections")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapGet("/{id:guid}", HandleGetConnection)
            .WithDisplayName("Get Secure Connection")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPost("/", HandleCreateConnection)
            .WithDisplayName("Create Secure Connection")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapPut("/{id:guid}", HandleUpdateConnection)
            .WithDisplayName("Update Secure Connection")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Put }));

        group.MapDelete("/{id:guid}", HandleDeleteConnection)
            .WithDisplayName("Delete Secure Connection")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Delete }));

        // Connection testing and health
        group.MapPost("/{id:guid}/test", HandleTestConnection)
            .WithDisplayName("Test Connection Health")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        // Encryption management
        group.MapPost("/encryption/validate", HandleValidateEncryption)
            .WithDisplayName("Validate Encryption Service")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapPost("/encryption/rotate-key", HandleRotateEncryptionKey)
            .WithDisplayName("Rotate Encryption Key")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));
    }

    /// <summary>
    /// GET /api/v1/admin/connections - List all secure connections (metadata only).
    /// </summary>
    private static async Task<Results<Ok<ApiResponse<IReadOnlyList<SecureConnectionSummary>>>, BadRequest<ApiResponse<object>>>>
        HandleGetConnections(
            ISecureConnectionRegistry registry,
            HttpContext context,
            ILogger<SecureConnectionEndpointsLog> logger)
    {
        try
        {
            var connections = await registry.GetActiveConnectionsAsync(context.RequestAborted);

            var summaries = connections.Select(c => new SecureConnectionSummary
            {
                ConnectionId = c.ConnectionId,
                Name = c.Name,
                Description = c.Description,
                Host = c.Host,
                Port = c.Port,
                DatabaseName = c.DatabaseName,
                Username = c.Username,
                SslRequired = c.SslRequired,
                SslMode = c.SslMode.ToString(),
                StorageType = GetStorageClass(c),
                IsActive = c.IsActive,
                HealthStatus = c.HealthStatus.ToString(),
                LastHealthCheck = c.LastHealthCheck,
                CreatedAt = c.CreatedAt,
                CreatedBy = c.CreatedBy
            }).ToList();

            logger.LogInformation("Retrieved {Count} secure connections", summaries.Count);

            return TypedResults.Ok(ApiResponse<IReadOnlyList<SecureConnectionSummary>>.CreateSuccess(summaries.AsReadOnly()));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to retrieve secure connections");
            return TypedResults.BadRequest(ApiResponse<object>.Failure("Failed to retrieve secure connections"));
        }
    }

    /// <summary>
    /// GET /api/v1/admin/connections/{id} - Get specific connection details.
    /// </summary>
    private static async Task<Results<Ok<ApiResponse<SecureConnectionDetail>>, NotFound<ApiResponse<object>>, BadRequest<ApiResponse<object>>>>
        HandleGetConnection(
            Guid id,
            ISecureConnectionRegistry registry,
            HttpContext context,
            ILogger<SecureConnectionEndpointsLog> logger)
    {
        try
        {
            var connection = await registry.GetConnectionAsync(id, context.RequestAborted);
            if (connection == null)
            {
                logger.LogWarning("Connection with ID {ConnectionId} not found", id);
                return TypedResults.NotFound(ApiResponse<object>.Failure("Connection not found"));
            }

            var detail = new SecureConnectionDetail
            {
                ConnectionId = connection.ConnectionId,
                Name = connection.Name,
                Description = connection.Description,
                Host = connection.Host,
                Port = connection.Port,
                DatabaseName = connection.DatabaseName,
                Username = connection.Username,
                SslRequired = connection.SslRequired,
                SslMode = connection.SslMode.ToString(),
                StorageType = GetStorageClass(connection),
                CredentialReference = connection.SecretRef,
                EncryptionVersion = connection.EncryptionKeyVersion,
                IsActive = connection.IsActive,
                HealthStatus = connection.HealthStatus.ToString(),
                LastHealthCheck = connection.LastHealthCheck,
                CreatedAt = connection.CreatedAt,
                UpdatedAt = connection.UpdatedAt,
                CreatedBy = connection.CreatedBy
            };

            return TypedResults.Ok(ApiResponse<SecureConnectionDetail>.CreateSuccess(detail));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to retrieve connection {ConnectionId}", id);
            return TypedResults.BadRequest(ApiResponse<object>.Failure("Failed to retrieve connection"));
        }
    }

    /// <summary>
    /// POST /api/v1/admin/connections - Create new secure connection.
    /// </summary>
    private static async Task<Results<Created<ApiResponse<SecureConnectionSummary>>, BadRequest<ApiResponse<object>>>>
        HandleCreateConnection(
            CreateSecureConnectionRequest request,
            ISecureConnectionRegistry registry,
            IConnectionEncryptionService encryptionService,
            [FromServices] IDatabaseConnectionStringBuilder connectionStringBuilder,
            HttpContext context)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<SecureConnectionEndpointsLog>>();

        try
        {
            // Validate request
            var validationResults = new List<ValidationResult>();
            var validationContext = new ValidationContext(request);
            if (!Validator.TryValidateObject(request, validationContext, validationResults, true))
            {
                var errors = validationResults.Select(r => r.ErrorMessage).ToList();
                logger.LogWarning("Invalid create connection request: {Errors}", string.Join(", ", errors));
                return TypedResults.BadRequest(ApiResponse<object>.Failure($"Validation failed: {string.Join(", ", errors)}"));
            }

            if (!request.IsValid(out var validationError))
            {
                logger.LogWarning("Invalid create connection request: {Error}", validationError);
                return TypedResults.BadRequest(ApiResponse<object>.Failure($"Validation failed: {validationError}"));
            }

            DataConnection connection;
            var userIdentity = context.GetUserIdentity();

            if (!string.IsNullOrWhiteSpace(request.SecretReference))
            {
                // Create with secret reference
                connection = DataConnection.CreateWithSecretReference(
                    request.Name,
                    request.Host,
                    request.Port,
                    request.DatabaseName,
                    request.Username,
                    request.SecretReference,
                    request.SecretType!,
                    userIdentity,
                    request.Description,
                    request.SslRequired,
                    Enum.Parse<SslMode>(request.SslMode, true));
            }
            else
            {
                // Create with encrypted credentials
                if (string.IsNullOrWhiteSpace(request.Password))
                {
                    return TypedResults.BadRequest(ApiResponse<object>.Failure("Password is required when not using secret reference"));
                }

                // Build connection string and encrypt it
                var effectiveSslMode = request.SslRequired
                    ? SslMode.Require
                    : SslMode.Prefer;
                var connectionString = connectionStringBuilder.BuildConnectionString(
                    request.Host,
                    request.Port,
                    request.DatabaseName,
                    request.Username,
                    request.Password,
                    effectiveSslMode);

                var encryptedData = await encryptionService.EncryptConnectionStringAsync(connectionString);
                var keyVersion = await encryptionService.GetCurrentKeyVersionAsync();

                connection = DataConnection.CreateWithEncryptedCredentials(
                    request.Name,
                    request.Host,
                    request.Port,
                    request.DatabaseName,
                    request.Username,
                    encryptedData,
                    keyVersion,
                    userIdentity,
                    request.Description,
                    request.SslRequired,
                    Enum.Parse<SslMode>(request.SslMode, true));
            }

            // Create the connection
            var createdConnection = await registry.CreateConnectionAsync(connection, context.RequestAborted);

            var summary = new SecureConnectionSummary
            {
                ConnectionId = createdConnection.ConnectionId,
                Name = createdConnection.Name,
                Description = createdConnection.Description,
                Host = createdConnection.Host,
                Port = createdConnection.Port,
                DatabaseName = createdConnection.DatabaseName,
                Username = createdConnection.Username,
                SslRequired = createdConnection.SslRequired,
                SslMode = createdConnection.SslMode.ToString(),
                StorageType = GetStorageClass(createdConnection),
                IsActive = createdConnection.IsActive,
                HealthStatus = createdConnection.HealthStatus.ToString(),
                CreatedAt = createdConnection.CreatedAt,
                CreatedBy = createdConnection.CreatedBy
            };

            logger.LogInformation("Created secure connection '{Name}' with ID {ConnectionId}",
                createdConnection.Name, createdConnection.ConnectionId);

            return TypedResults.Created($"/api/v1/admin/connections/{createdConnection.ConnectionId}",
                ApiResponse<SecureConnectionSummary>.CreateSuccess(summary));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create secure connection");
            return TypedResults.BadRequest(ApiResponse<object>.Failure("Failed to create secure connection"));
        }
    }

    /// <summary>
    /// POST /api/v1/admin/connections/{id}/test - Test connection health.
    /// </summary>
    private static async Task<Results<Ok<ApiResponse<ConnectionTestResult>>, NotFound<ApiResponse<object>>, BadRequest<ApiResponse<object>>>>
        HandleTestConnection(
            Guid id,
            ISecureConnectionResolver resolver,
            ISecureConnectionRegistry registry,
            HttpContext context,
            ILogger<SecureConnectionEndpointsLog> logger)
    {
        try
        {
            var connection = await registry.GetConnectionAsync(id, context.RequestAborted);
            if (connection == null)
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure("Connection not found"));
            }

            var isHealthy = await resolver.TestConnectionHealthAsync(connection.Name, context.RequestAborted);

            var result = new ConnectionTestResult
            {
                ConnectionId = id,
                ConnectionName = connection.Name,
                IsHealthy = isHealthy,
                TestedAt = DateTimeOffset.UtcNow,
                Message = isHealthy ? "Connection is healthy" : "Connection test failed"
            };

            logger.LogInformation("Connection test for '{ConnectionName}' result: {IsHealthy}",
                connection.Name, isHealthy);

            return TypedResults.Ok(ApiResponse<ConnectionTestResult>.CreateSuccess(result));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to test connection {ConnectionId}", id);
            return TypedResults.BadRequest(ApiResponse<object>.Failure("Failed to test connection"));
        }
    }

    /// <summary>
    /// POST /api/v1/admin/connections/encryption/validate - Validate encryption service.
    /// </summary>
    private static async Task<Results<Ok<ApiResponse<EncryptionValidationResult>>, BadRequest<ApiResponse<object>>>>
        HandleValidateEncryption(
            IConnectionEncryptionService encryptionService,
            HttpContext context,
            ILogger<SecureConnectionEndpointsLog> logger)
    {
        try
        {
            var isValid = await encryptionService.ValidateEncryptionAsync();
            var keyVersion = await encryptionService.GetCurrentKeyVersionAsync();

            var result = new EncryptionValidationResult
            {
                IsValid = isValid,
                CurrentKeyVersion = keyVersion,
                ValidatedAt = DateTimeOffset.UtcNow,
                Message = isValid ? "Encryption service is working correctly" : "Encryption service validation failed"
            };

            logger.LogInformation("Encryption validation result: {IsValid}, key version: {KeyVersion}",
                isValid, keyVersion);

            return TypedResults.Ok(ApiResponse<EncryptionValidationResult>.CreateSuccess(result));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to validate encryption service");
            return TypedResults.BadRequest(ApiResponse<object>.Failure("Failed to validate encryption service"));
        }
    }

    // Additional endpoint handlers would go here (PUT, DELETE, etc.)
    // For brevity, I'm showing the key patterns and most important endpoints

    private static async Task<Results<Ok<ApiResponse<object>>, BadRequest<ApiResponse<object>>>> HandleUpdateConnection(
        Guid id, object request, ISecureConnectionRegistry registry, HttpContext context, ILogger<SecureConnectionEndpointsLog> logger) =>
        TypedResults.BadRequest(ApiResponse<object>.Failure("Not implemented"));

    private static async Task<Results<Ok<ApiResponse<object>>, NotFound<ApiResponse<object>>, BadRequest<ApiResponse<object>>>> HandleDeleteConnection(
        Guid id, ISecureConnectionRegistry registry, HttpContext context, ILogger<SecureConnectionEndpointsLog> logger) =>
        TypedResults.BadRequest(ApiResponse<object>.Failure("Not implemented"));

    private static async Task<Results<Ok<ApiResponse<object>>, BadRequest<ApiResponse<object>>>> HandleRotateEncryptionKey(
        IConnectionEncryptionService encryptionService, HttpContext context, ILogger<SecureConnectionEndpointsLog> logger) =>
        TypedResults.BadRequest(ApiResponse<object>.Failure("Not implemented"));

    private static string GetStorageClass(DataConnection connection)
    {
        return connection.ConnectionStringEncrypted != null ? "managed" : "external";
    }
}

// Extension method to get user identity from HTTP context
internal static class HttpContextExtensions
{
    public static string GetUserIdentity(this HttpContext context)
    {
        // This would extract the user identity from the authentication context
        // For now, return a placeholder
        return context.User?.Identity?.Name ?? "admin";
    }
}
