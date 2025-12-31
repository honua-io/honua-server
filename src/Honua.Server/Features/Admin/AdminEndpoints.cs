// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using System.Text.Json;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for table discovery and connection management
/// </summary>
internal static class AdminEndpoints
{
    /// <summary>
    /// Configure admin endpoints using AOT-compatible routing
    /// </summary>
    public static void MapAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Create admin group with authorization requirement
        RouteGroupBuilder adminGroup = endpoints.MapGroup("/api/admin")
            .WithTags("Admin")
            .RequireAdminAuthorization();

        // Use Map with explicit HTTP method metadata to avoid MapGet reflection
        _ = adminGroup.Map("/connections/{id}/tables", HandleGetConnectionTables)
            .WithDisplayName("Get Connection Tables")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        // Use catch-all parameter to handle edge cases like empty segments
        _ = adminGroup.Map("/connections/{*path}", HandleGetConnectionTablesWithCatchAll)
            .WithDisplayName("Get Connection Tables - Catch All")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
    }

    /// <summary>
    /// Handle catch-all cases for connections endpoints
    /// </summary>
    private static async Task HandleGetConnectionTablesWithCatchAll(HttpContext context)
    {
        string path = context.GetRouteValue("path")?.ToString() ?? "";

        // Check if this is the tables endpoint with empty or invalid connection ID
        if (path.Equals("/tables", StringComparison.OrdinalIgnoreCase) ||
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
    /// Implements the API from Issue #57: GET /api/admin/connections/{id}/tables
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
            // For this initial implementation, use the default database connection for all connection IDs
            // In a full implementation, this would look up the connection by ID and validate it exists
            IDatabaseConnectionProvider connectionProvider = context.RequestServices.GetRequiredService<IDatabaseConnectionProvider>();
            ITableDiscoveryService tableDiscoveryService = context.RequestServices.GetRequiredService<ITableDiscoveryService>();

            await using DbConnection connection = await connectionProvider.OpenConnectionAsync(context.RequestAborted);

            // Pass the opened connection directly to avoid password extraction issues
            List<TableInfo> tables = await tableDiscoveryService.DiscoverPostGisTablesAsync(
                connection,
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
