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
internal static partial class SecureConnectionEndpoints
{
    /// <summary>
    /// Log category for secure connection endpoints.
    /// </summary>
    internal sealed class SecureConnectionEndpointsLog;

    /// <summary>
    /// Source-generated logging methods for secure connection endpoints.
    /// </summary>
    internal static partial class SecureConnectionLog
    {
        [LoggerMessage(EventId = 4400, Level = LogLevel.Information,
            Message = "Retrieved {Count} secure connections")]
        public static partial void ConnectionsRetrieved(ILogger logger, int count);

        [LoggerMessage(EventId = 4401, Level = LogLevel.Information,
            Message = "Created secure connection '{Name}' with ID {ConnectionId}")]
        public static partial void SecureConnectionCreated(ILogger logger, string name, Guid connectionId);

        [LoggerMessage(EventId = 4402, Level = LogLevel.Information,
            Message = "Connection test for '{ConnectionName}' result: {IsHealthy}")]
        public static partial void ConnectionTestCompleted(ILogger logger, string connectionName, bool isHealthy);

        [LoggerMessage(EventId = 4403, Level = LogLevel.Information,
            Message = "Encryption validation result: {IsValid}, key version: {KeyVersion}")]
        public static partial void EncryptionValidated(ILogger logger, bool isValid, int keyVersion);

        [LoggerMessage(EventId = 4404, Level = LogLevel.Information,
            Message = "Deleted secure connection {ConnectionId}")]
        public static partial void SecureConnectionDeleted(ILogger logger, Guid connectionId);
    }
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

        group.MapPost("/test", HandleTestDraftConnection)
            .WithDisplayName("Test Secure Connection Draft")
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
    /// POST /api/v1/admin/connections/test - Test a draft connection before saving.
    /// </summary>
    private static async Task<Results<Ok<ApiResponse<ConnectionTestResult>>, BadRequest<ApiResponse<object>>, ProblemHttpResult>>
        HandleTestDraftConnection(
            CreateSecureConnectionRequest request,
            [FromServices] IDatabaseConnectionStringBuilder connectionStringBuilder,
            [FromServices] IConnectionSecretResolver secretResolver,
            [FromServices] IConnectionHealthTester connectionTester,
            HttpContext context,
            [FromServices] ILogger<SecureConnectionEndpointsLog> logger)
    {
        try
        {
            var validationResults = new List<ValidationResult>();
            var validationContext = new ValidationContext(request);
            if (!Validator.TryValidateObject(request, validationContext, validationResults, true))
            {
                var errors = validationResults.Select(r => r.ErrorMessage).ToList();
                logger.LogWarning("Invalid test connection request: {Errors}", string.Join(", ", errors));
                return TypedResults.BadRequest(ApiResponse<object>.Failure($"Validation failed: {string.Join(", ", errors)}"));
            }

            if (!request.IsValid(out var validationError))
            {
                logger.LogWarning("Invalid test connection request: {Error}", validationError);
                return TypedResults.BadRequest(ApiResponse<object>.Failure($"Validation failed: {validationError}"));
            }

            if (!Enum.TryParse<SslMode>(request.SslMode, true, out var parsedSslMode))
            {
                return TypedResults.BadRequest(ApiResponse<object>.Failure("Invalid SSL mode"));
            }

            if (request.SslRequired && parsedSslMode == SslMode.Disable)
            {
                return TypedResults.BadRequest(ApiResponse<object>.Failure("SSL mode 'Disable' is invalid when SSL is required"));
            }

            string connectionString;

            if (!string.IsNullOrWhiteSpace(request.SecretReference))
            {
                connectionString = await secretResolver.ResolveConnectionStringAsync(
                    request.SecretReference,
                    context.RequestAborted);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(request.Password))
                {
                    return TypedResults.BadRequest(ApiResponse<object>.Failure("Password is required when not using secret reference"));
                }

                connectionString = connectionStringBuilder.BuildConnectionString(
                    request.Host,
                    request.Port,
                    request.DatabaseName,
                    request.Username,
                    request.Password,
                    parsedSslMode);
            }

            var isHealthy = await connectionTester.TestConnectionAsync(connectionString, context.RequestAborted);
            var result = new ConnectionTestResult
            {
                ConnectionId = Guid.Empty,
                ConnectionName = request.Name,
                IsHealthy = isHealthy,
                TestedAt = DateTimeOffset.UtcNow,
                Message = isHealthy ? "Connection is healthy" : "Connection test failed"
            };

            return TypedResults.Ok(ApiResponse<ConnectionTestResult>.CreateSuccess(result));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to test draft connection");
            return TypedResults.Problem(
                title: "Draft connection test failed",
                detail: "An internal error occurred while testing the draft connection.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// GET /api/v1/admin/connections - List all secure connections (metadata only).
    /// </summary>
    private static async Task<Results<Ok<ApiResponse<IReadOnlyList<SecureConnectionSummary>>>, ProblemHttpResult>>
        HandleGetConnections(
            [FromServices] ISecureConnectionRegistry registry,
            HttpContext context,
            [FromServices] ILogger<SecureConnectionEndpointsLog> logger)
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

            SecureConnectionLog.ConnectionsRetrieved(logger, summaries.Count);

            return TypedResults.Ok(ApiResponse<IReadOnlyList<SecureConnectionSummary>>.CreateSuccess(summaries.AsReadOnly()));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to retrieve secure connections");
            return TypedResults.Problem(
                title: "Secure connection listing failed",
                detail: "An internal error occurred while retrieving secure connections.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// GET /api/v1/admin/connections/{id} - Get specific connection details.
    /// </summary>
    private static async Task<Results<Ok<ApiResponse<SecureConnectionDetail>>, NotFound<ApiResponse<object>>, ProblemHttpResult>>
        HandleGetConnection(
            Guid id,
            [FromServices] ISecureConnectionRegistry registry,
            HttpContext context,
            [FromServices] ILogger<SecureConnectionEndpointsLog> logger)
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
            return TypedResults.Problem(
                title: "Secure connection retrieval failed",
                detail: "An internal error occurred while retrieving the secure connection.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// POST /api/v1/admin/connections - Create new secure connection.
    /// </summary>
    private static async Task<Results<Created<ApiResponse<SecureConnectionSummary>>, BadRequest<ApiResponse<object>>, ProblemHttpResult>>
        HandleCreateConnection(
            CreateSecureConnectionRequest request,
            [FromServices] ISecureConnectionRegistry registry,
            [FromServices] IConnectionEncryptionService encryptionService,
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

            if (!Enum.TryParse<SslMode>(request.SslMode, true, out var parsedSslMode))
            {
                return TypedResults.BadRequest(ApiResponse<object>.Failure("Invalid SSL mode"));
            }

            if (request.SslRequired && parsedSslMode == SslMode.Disable)
            {
                return TypedResults.BadRequest(ApiResponse<object>.Failure("SSL mode 'Disable' is invalid when SSL is required"));
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
                    parsedSslMode);
            }
            else
            {
                // Create with encrypted credentials
                if (string.IsNullOrWhiteSpace(request.Password))
                {
                    return TypedResults.BadRequest(ApiResponse<object>.Failure("Password is required when not using secret reference"));
                }

                // Build connection string and encrypt it
                var connectionString = connectionStringBuilder.BuildConnectionString(
                    request.Host,
                    request.Port,
                    request.DatabaseName,
                    request.Username,
                    request.Password,
                    parsedSslMode);

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
                    parsedSslMode);
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

            SecureConnectionLog.SecureConnectionCreated(logger, createdConnection.Name, createdConnection.ConnectionId);

            return TypedResults.Created($"/api/v1/admin/connections/{createdConnection.ConnectionId}",
                ApiResponse<SecureConnectionSummary>.CreateSuccess(summary));
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Failed to create secure connection due to invalid operation");
            return TypedResults.BadRequest(ApiResponse<object>.Failure(ex.Message));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create secure connection");
            return TypedResults.Problem(
                title: "Secure connection creation failed",
                detail: "An internal error occurred while creating the secure connection.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// POST /api/v1/admin/connections/{id}/test - Test connection health.
    /// </summary>
    private static async Task<Results<Ok<ApiResponse<ConnectionTestResult>>, NotFound<ApiResponse<object>>, ProblemHttpResult>>
        HandleTestConnection(
            Guid id,
            [FromServices] ISecureConnectionResolver resolver,
            [FromServices] ISecureConnectionRegistry registry,
            HttpContext context,
            [FromServices] ILogger<SecureConnectionEndpointsLog> logger)
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

            SecureConnectionLog.ConnectionTestCompleted(logger, connection.Name, isHealthy);

            return TypedResults.Ok(ApiResponse<ConnectionTestResult>.CreateSuccess(result));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to test connection {ConnectionId}", id);
            return TypedResults.Problem(
                title: "Secure connection test failed",
                detail: "An internal error occurred while testing the secure connection.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// POST /api/v1/admin/connections/encryption/validate - Validate encryption service.
    /// </summary>
    private static async Task<Results<Ok<ApiResponse<EncryptionValidationResult>>, ProblemHttpResult>>
        HandleValidateEncryption(
            [FromServices] IConnectionEncryptionService encryptionService,
            HttpContext context,
            [FromServices] ILogger<SecureConnectionEndpointsLog> logger)
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

            SecureConnectionLog.EncryptionValidated(logger, isValid, keyVersion);

            return TypedResults.Ok(ApiResponse<EncryptionValidationResult>.CreateSuccess(result));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to validate encryption service");
            return TypedResults.Problem(
                title: "Encryption validation failed",
                detail: "An internal error occurred while validating encryption.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<SecureConnectionSummary>>, NotFound<ApiResponse<object>>, BadRequest<ApiResponse<object>>, ProblemHttpResult>>
        HandleUpdateConnection(
            Guid id,
            UpdateSecureConnectionRequest request,
            [FromServices] ISecureConnectionRegistry registry,
            [FromServices] IConnectionEncryptionService encryptionService,
            [FromServices] IDatabaseConnectionStringBuilder connectionStringBuilder,
            HttpContext context,
            [FromServices] ILogger<SecureConnectionEndpointsLog> logger)
    {
        try
        {
            var validationResults = new List<ValidationResult>();
            var validationContext = new ValidationContext(request);
            if (!Validator.TryValidateObject(request, validationContext, validationResults, true))
            {
                var errors = validationResults.Select(r => r.ErrorMessage).ToList();
                logger.LogWarning("Invalid update connection request: {Errors}", string.Join(", ", errors));
                return TypedResults.BadRequest(ApiResponse<object>.Failure($"Validation failed: {string.Join(", ", errors)}"));
            }

            var existing = await registry.GetConnectionAsync(id, context.RequestAborted);
            if (existing == null)
            {
                logger.LogWarning("Connection with ID {ConnectionId} not found for update", id);
                return TypedResults.NotFound(ApiResponse<object>.Failure("Connection not found"));
            }

            var host = request.Host ?? existing.Host;
            var port = request.Port ?? existing.Port;
            var databaseName = request.DatabaseName ?? existing.DatabaseName;
            var username = request.Username ?? existing.Username;
            var description = request.Description ?? existing.Description;
            var sslRequired = request.SslRequired ?? existing.SslRequired;
            var isActive = request.IsActive ?? existing.IsActive;

            var sslMode = existing.SslMode;
            if (!string.IsNullOrWhiteSpace(request.SslMode))
            {
                if (!Enum.TryParse<SslMode>(request.SslMode, true, out var parsedSslMode))
                {
                    return TypedResults.BadRequest(ApiResponse<object>.Failure("Invalid SSL mode"));
                }
                sslMode = parsedSslMode;
            }

            if (sslRequired && sslMode == SslMode.Disable)
            {
                return TypedResults.BadRequest(ApiResponse<object>.Failure("SSL mode 'Disable' is invalid when SSL is required"));
            }

            byte[]? encryptedConnection = existing.ConnectionStringEncrypted;
            int encryptionVersion = existing.EncryptionKeyVersion;
            string? secretRef = existing.SecretRef;
            string? secretType = existing.SecretType;

            if (!string.IsNullOrWhiteSpace(request.Password))
            {
                var connectionString = connectionStringBuilder.BuildConnectionString(
                    host,
                    port,
                    databaseName,
                    username,
                    request.Password,
                    sslMode);

                encryptedConnection = await encryptionService.EncryptConnectionStringAsync(connectionString);
                encryptionVersion = await encryptionService.GetCurrentKeyVersionAsync();
                secretRef = null;
                secretType = null;
            }

            var updatedConnection = new DataConnection
            {
                ConnectionId = existing.ConnectionId,
                Name = existing.Name,
                Description = description,
                Host = host,
                Port = port,
                DatabaseName = databaseName,
                Username = username,
                SslRequired = sslRequired,
                SslMode = sslMode,
                ConnectionStringEncrypted = encryptedConnection,
                EncryptionKeyVersion = encryptionVersion,
                SecretRef = secretRef,
                SecretType = secretType,
                CreatedAt = existing.CreatedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
                CreatedBy = existing.CreatedBy,
                IsActive = isActive,
                LastHealthCheck = existing.LastHealthCheck,
                HealthStatus = existing.HealthStatus
            };

            var saved = await registry.UpdateConnectionAsync(updatedConnection, context.RequestAborted);

            var summary = new SecureConnectionSummary
            {
                ConnectionId = saved.ConnectionId,
                Name = saved.Name,
                Description = saved.Description,
                Host = saved.Host,
                Port = saved.Port,
                DatabaseName = saved.DatabaseName,
                Username = saved.Username,
                SslRequired = saved.SslRequired,
                SslMode = saved.SslMode.ToString(),
                StorageType = GetStorageClass(saved),
                IsActive = saved.IsActive,
                HealthStatus = saved.HealthStatus.ToString(),
                LastHealthCheck = saved.LastHealthCheck,
                CreatedAt = saved.CreatedAt,
                CreatedBy = saved.CreatedBy
            };

            return TypedResults.Ok(ApiResponse<SecureConnectionSummary>.CreateSuccess(summary));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update secure connection {ConnectionId}", id);
            return TypedResults.Problem(
                title: "Secure connection update failed",
                detail: "An internal error occurred while updating the secure connection.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<object>>, NotFound<ApiResponse<object>>, Conflict<ApiResponse<object>>, ProblemHttpResult>>
        HandleDeleteConnection(
            Guid id,
            [FromServices] ISecureConnectionRegistry registry,
            HttpContext context,
            [FromServices] ILogger<SecureConnectionEndpointsLog> logger)
    {
        try
        {
            var deleted = await registry.DeleteConnectionAsync(id, context.RequestAborted);
            if (!deleted)
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure("Connection not found"));
            }

            SecureConnectionLog.SecureConnectionDeleted(logger, id);
            return TypedResults.Ok(ApiResponse<object>.SuccessWithMessage("Connection deleted"));
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "23503")
        {
            logger.LogWarning(ex, "Secure connection {ConnectionId} is in use", id);
            return TypedResults.Conflict(ApiResponse<object>.Failure("Connection is in use by services"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete secure connection {ConnectionId}", id);
            return TypedResults.Problem(
                title: "Secure connection deletion failed",
                detail: "An internal error occurred while deleting the secure connection.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<KeyRotationResult>>, BadRequest<ApiResponse<object>>, ProblemHttpResult>>
        HandleRotateEncryptionKey(
            [FromServices] IConnectionEncryptionService encryptionService,
            HttpContext context,
            [FromServices] ILogger<SecureConnectionEndpointsLog> logger)
    {
        try
        {
            var previousVersion = await encryptionService.GetCurrentKeyVersionAsync();
            var newVersion = await encryptionService.RotateKeyAsync();

            var result = new KeyRotationResult
            {
                PreviousKeyVersion = previousVersion,
                NewKeyVersion = newVersion,
                RotatedAt = DateTimeOffset.UtcNow,
                Message = "Encryption key rotated successfully"
            };

            logger.LogWarning("Encryption key rotated from {Previous} to {New}", previousVersion, newVersion);

            return TypedResults.Ok(ApiResponse<KeyRotationResult>.CreateSuccess(result));
        }
        catch (NotSupportedException ex)
        {
            logger.LogWarning(ex, "Encryption key rotation is not supported");
            return TypedResults.BadRequest(ApiResponse<object>.Failure(ex.Message));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to rotate encryption key");
            return TypedResults.Problem(
                title: "Encryption key rotation failed",
                detail: "An internal error occurred while rotating the encryption key.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

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
