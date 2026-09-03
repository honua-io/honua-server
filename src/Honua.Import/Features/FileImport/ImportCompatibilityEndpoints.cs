// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.Authentication;

namespace Honua.Import.FileImport;

internal static partial class ImportEndpoints
{
    // Keep the compatibility group separate from the primary import route file so the
    // source-based 405 responder derivation can unambiguously resolve its one route group.
    private static void MapCompatibilityImportRoutes(WebApplication app)
    {
        // These routes deliberately point at the same downloader, validator, queue, and
        // progress store as /import/upload-url; they are real import operations, not aliases.
        var importsGroup = app.MapGroup("/api/v{version:apiVersion}/admin/imports");
        importsGroup.WithApiVersionSet().HasApiVersion(1, 0);
        importsGroup.WithTags("Admin", "Import");
        importsGroup.RequireAdminAuthorization();

        importsGroup.MapPost(string.Empty, HandleImportFileFromUrl)
            .WithSummary("Create and queue an import job from a source URL.");

        importsGroup.MapGet("/jobs", HandleGetActiveJobs);
        importsGroup.MapGet("/jobs/{jobId}", HandleGetImportJobStatus);
        importsGroup.MapPost("/jobs/{jobId}/cancel", HandleCancelImportJob);
    }
}
