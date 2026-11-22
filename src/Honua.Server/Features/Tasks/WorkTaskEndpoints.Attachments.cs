// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Asp.Versioning.Builder;
using Honua.Core.Features.Assets;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Tasks;

internal static partial class WorkTaskEndpoints
{
    private static void MapAttachmentEndpoints(IVersionedEndpointRouteBuilder tasks)
    {
        tasks.MapPost("/{taskId:guid}/attachments", HandleUploadAttachment)
            .WithName("UploadWorkTaskAttachment")
            .WithSummary("Upload evidence for an assigned, in-progress task using its pinned form policy.")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]))
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<WorkTaskAttachmentResponse>(StatusCodes.Status201Created)
            .Produces<AssetValidationProblem>(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        tasks.MapGet("/{taskId:guid}/attachments/{attachmentId:guid}", HandleDownloadAttachment)
            .WithName("DownloadWorkTaskAttachment")
            .WithSummary("Download authorized task evidence.")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]))
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static Task<IResult> HandleUploadAttachment(HttpContext context,
        [FromServices] WorkTaskAttachmentService service, Guid taskId)
        => service.UploadAsync(context, taskId);

    private static Task<IResult> HandleDownloadAttachment(HttpContext context,
        [FromServices] WorkTaskAttachmentService service, [AsParameters] AttachmentRoute route)
        => service.DownloadAsync(context, (route.TaskId, route.AttachmentId));

    internal readonly record struct AttachmentRoute(Guid TaskId, Guid AttachmentId);
}
