// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Infrastructure.Services;

namespace Honua.Server.Features.Infrastructure.Extensions;

/// <summary>
/// Extension methods for configuring temporary file endpoints.
/// </summary>
public static class TemporaryFileEndpointExtensions
{
    /// <summary>
    /// Maps temporary file serving endpoints.
    /// </summary>
    public static IEndpointRouteBuilder MapTemporaryFileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/temp")
            .WithTags("Temporary Files")
            .WithDisplayName("Temporary File Service");

        // Serve temporary files by ID
        group.MapGet("/{fileId}", GetTemporaryFile)
            .WithName("GetTemporaryFile")
            .WithSummary("Get temporary file by ID")
            .WithDescription("Retrieves a temporary file by its identifier. Files expire after their configured lifetime.")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> GetTemporaryFile(
        string fileId,
        HttpContext context,
        ITemporaryFileService temporaryFileService,
        CancellationToken cancellationToken = default)
    {
        var result = await temporaryFileService.GetTemporaryFileAsync(
            fileId,
            principal: context.User,
            cancellationToken: cancellationToken);

        if (result == null)
        {
            return Results.NotFound();
        }

        var (data, contentType) = result.Value;
        return Results.File(data, contentType);
    }
}
