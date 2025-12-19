// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Infrastructure.Abstractions;
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
    public static void MapAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Use Map with explicit HTTP method metadata to avoid MapGet reflection
        endpoints.Map("/api/admin/connections/{id}/tables", HandleGetConnectionTables)
            .WithDisplayName("Get Connection Tables")
            .WithTags("Admin")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
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
            context.Response.StatusCode = 405; // Method Not Allowed
            context.Response.ContentType = "application/problem+json; charset=utf-8";
            await context.Response.WriteAsync($$"""{"title":"Method Not Allowed","status":405,"detail":"Only GET requests are allowed for this endpoint"}""");
            return;
        }

        // Extract connection ID from route
        var id = context.GetRouteValue("id")?.ToString();

        // Validate input
        if (string.IsNullOrWhiteSpace(id))
        {
            context.Response.StatusCode = 400; // Bad Request
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync("Connection ID is required");
            return;
        }

        var logger = context.RequestServices.GetRequiredService<ILogger<AdminEndpoints>>();

        try
        {
            // For now, use the default database connection
            // In a full implementation, this would look up the connection by ID
            var connectionProvider = context.RequestServices.GetRequiredService<IDatabaseConnectionProvider>();

            await using var connection = await connectionProvider.OpenConnectionAsync(context.RequestAborted);
            var connectionString = connection.ConnectionString;

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
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json; charset=utf-8";
            await JsonSerializer.SerializeAsync(context.Response.Body, response,
                TableDiscoveryJsonContext.Default.TableDiscoveryResponse,
                context.RequestAborted);
        }
        catch (Exception ex)
        {
            AdminLog.TableDiscoveryFailed(logger, ex, id);

            context.Response.StatusCode = 500; // Internal Server Error
            context.Response.ContentType = "application/problem+json; charset=utf-8";
            await context.Response.WriteAsync($$"""{"title":"Table Discovery Failed","status":500,"detail":"An error occurred while discovering tables. Please check the connection and try again."}""");
        }
    }
}
