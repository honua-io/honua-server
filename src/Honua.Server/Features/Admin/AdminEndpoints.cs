// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Admin.Services;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for table discovery and connection management
/// </summary>
internal static class AdminEndpoints
{
    /// <summary>
    /// Configure admin endpoints using AOT-compatible routing with formal API versioning
    /// </summary>
    public static void MapAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Create admin group with authorization requirement and formal versioning
        var adminGroup = endpoints.MapGroup("/api/v{version:apiVersion}/admin")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin")
            .RequireAdminAuthorization();

        // Configuration documentation endpoint (self-documenting)
        _ = adminGroup.Map("/config", HandleGetConfiguration)
            .WithDisplayName("Get Configuration Documentation")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        // Use Map with explicit HTTP method metadata to avoid MapGet reflection
        _ = adminGroup.Map("/connections/{id}/tables", HandleGetConnectionTables)
            .WithDisplayName("Get Connection Tables")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        _ = adminGroup.Map("/connections/tables", HandleGetConnectionTablesWithCatchAll)
            .WithDisplayName("Get Connection Tables - Missing ID")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        // Use catch-all parameter to handle edge cases like empty segments
        _ = adminGroup.Map("/connections/{*path}", HandleGetConnectionTablesWithCatchAll)
            .WithDisplayName("Get Connection Tables - Catch All")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
    }

    /// <summary>
    /// Handle GET /api/v1/admin/config - Self-documenting configuration endpoint.
    /// Returns all available configuration options, their current values, and environment variable mappings.
    /// </summary>
    private static async Task HandleGetConfiguration(HttpContext context)
    {
        // Ensure only GET requests
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await ProblemDetailsHelpers.CreateAdminProblem(
                    context,
                    StatusCodes.Status405MethodNotAllowed,
                    "Only GET requests are allowed for this endpoint")
                .ExecuteAsync(context);
            return;
        }

        var configService = context.RequestServices.GetRequiredService<ConfigurationDocumentationService>();
        var documentation = configService.BuildDocumentation();

        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            documentation,
            ConfigurationJsonContext.Default.ConfigurationDocumentation,
            context.RequestAborted);
    }

    /// <summary>
    /// Handle catch-all cases for connections endpoints
    /// </summary>
    private static async Task HandleGetConnectionTablesWithCatchAll(HttpContext context)
    {
        string path = context.GetRouteValue("path")?.ToString() ?? "";

        // Check if this is the tables endpoint with empty or invalid connection ID
        if (string.IsNullOrWhiteSpace(path) ||
            path.Equals("/tables", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("tables", StringComparison.OrdinalIgnoreCase))
        {
            await ProblemDetailsHelpers.CreateAdminProblem(
                    context,
                    StatusCodes.Status400BadRequest,
                    "Connection ID is required")
                .ExecuteAsync(context);
            return;
        }

        // For other paths, return 404
        await ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status404NotFound,
                "The requested resource was not found.")
            .ExecuteAsync(context);
    }

    /// <summary>
    /// Handle admin connection tables request
    /// Implements the API from Issue #57: GET /api/v1/admin/connections/{id}/tables
    /// </summary>
    private static async Task HandleGetConnectionTables(HttpContext context)
    {
        // Ensure only GET requests
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await ProblemDetailsHelpers.CreateAdminProblem(
                    context,
                    StatusCodes.Status405MethodNotAllowed,
                    "Only GET requests are allowed for this endpoint")
                .ExecuteAsync(context);
            return;
        }

        // Extract connection ID from route
        string? id = context.GetRouteValue("id")?.ToString();

        // Validate input
        if (string.IsNullOrWhiteSpace(id))
        {
            await ProblemDetailsHelpers.CreateAdminProblem(
                    context,
                    StatusCodes.Status400BadRequest,
                    "Connection ID is required")
                .ExecuteAsync(context);
            return;
        }

        ILogger logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Admin.TableDiscovery");

        try
        {
            var resolver = context.RequestServices.GetRequiredService<ISecureConnectionResolver>();
            ITableDiscoveryService tableDiscoveryService = context.RequestServices.GetRequiredService<ITableDiscoveryService>();

            string connectionString;
            try
            {
                if (Guid.TryParse(id, out var connectionId))
                {
                    connectionString = await resolver.ResolveConnectionStringAsync(connectionId, context.RequestAborted);
                }
                else
                {
                    connectionString = await resolver.ResolveConnectionStringAsync(id, context.RequestAborted);
                }
            }
            catch (ArgumentException)
            {
                await ProblemDetailsHelpers.CreateAdminProblem(
                        context,
                        StatusCodes.Status400BadRequest,
                        "Invalid connection identifier")
                    .ExecuteAsync(context);
                return;
            }
            catch (InvalidOperationException)
            {
                await ProblemDetailsHelpers.CreateAdminProblem(
                        context,
                        StatusCodes.Status404NotFound,
                        "Connection not found")
                    .ExecuteAsync(context);
                return;
            }
            catch (UnauthorizedAccessException)
            {
                await ProblemDetailsHelpers.CreateAdminProblem(
                        context,
                        StatusCodes.Status403Forbidden,
                        "Connection access denied")
                    .ExecuteAsync(context);
                return;
            }

            List<TableInfo> tables = await tableDiscoveryService.DiscoverPostGisTablesAsync(
                connectionString,
                context.RequestAborted);

            var response = new TableDiscoveryResponse
            {
                Tables = tables
            };

            AdminLog.TableDiscoverySuccessful(logger, tables.Count, id);

            // Return JSON response with AOT-compatible serialization
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json; charset=utf-8";
            await JsonSerializer.SerializeAsync(context.Response.Body, response,
                TableDiscoveryJsonContext.Default.TableDiscoveryResponse,
                context.RequestAborted);
        }
        catch (Exception ex)
        {
            AdminLog.TableDiscoveryFailed(logger, ex, id);

            await ProblemDetailsHelpers.CreateAdminProblem(
                    context,
                    StatusCodes.Status500InternalServerError,
                    "Table Discovery Failed",
                    "An error occurred while discovering tables. Please check the connection and try again.")
                .ExecuteAsync(context);
        }
    }
}
