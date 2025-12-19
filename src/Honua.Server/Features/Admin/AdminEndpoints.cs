// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Admin.Services;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for table discovery and connection management
/// </summary>
public static class AdminEndpoints
{
    /// <summary>
    /// Configure admin endpoints using AOT-compatible routing
    /// </summary>
    [RequiresUnreferencedCode()]
    [RequiresDynamicCode()]
    public static void MapAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Use direct route mapping for AOT compatibility
        endpoints.Map("/api/admin/connections/{id}/tables", (Delegate)GetConnectionTables)
            .WithDisplayName("Get Connection Tables")
            .WithTags("Admin")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
    }

    /// <summary>
    /// Get all spatial tables for a connection
    /// Implements the API from Issue #57: GET /api/admin/connections/{id}/tables
    /// </summary>
    private static async Task<IResult> GetConnectionTables(
        string id,
        HttpContext context)
    {
        // Ensure only GET requests
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            return Results.Problem(
                title: "Method Not Allowed",
                statusCode: 405,
                detail: "Only GET requests are allowed for this endpoint");
        }

        // Validate input
        if (string.IsNullOrWhiteSpace(id))
        {
            return Results.BadRequest("Connection ID is required");
        }

        var logger = context.RequestServices.GetRequiredService<ILogger<ITableDiscoveryService>>();

        try
        {
            // For now, get connection string from configuration
            // In a full implementation, this would look up the connection by ID
            var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
            var connectionString = configuration.GetConnectionString("honua");

            if (string.IsNullOrEmpty(connectionString))
            {
                AdminLog.ConnectionNotFound(logger, id);
                return Results.NotFound($"Connection '{id}' not found");
            }

            var tableDiscoveryService = context.RequestServices.GetRequiredService<ITableDiscoveryService>();
            var tables = await tableDiscoveryService.DiscoverPostGisTablesAsync(
                connectionString,
                context.RequestAborted);

            var response = new TableDiscoveryResponse
            {
                Tables = tables
            };

            AdminLog.TableDiscoverySuccessful(logger, tables.Count, id);

            // Return JSON response with AOT-compatible serialization
            return Results.Json(response, TableDiscoveryJsonContext.Default.TableDiscoveryResponse);
        }
        catch (Exception ex)
        {
            AdminLog.TableDiscoveryFailed(logger, ex, id);

            return Results.Problem(
                title: "Table Discovery Failed",
                statusCode: 500,
                detail: "An error occurred while discovering tables. Please check the connection and try again.");
        }
    }
}
