// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FieldWorkflows.Review;
using Honua.Infrastructure.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.FieldWorkflows.Review;

/// <summary>
/// Admin REST surface for back-office field data review and QA (#1159). Every
/// route requires admin authorization and adapts to the shared
/// <see cref="FieldReviewService"/>; all state transitions are audited.
/// </summary>
internal static class FieldReviewEndpoints
{
    public static IEndpointRouteBuilder MapFieldReviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var serviceInspector = endpoints.ServiceProvider.GetService<IServiceProviderIsService>();
        if (serviceInspector is not null && !serviceInspector.IsService(typeof(IFieldReviewStore)))
        {
            return endpoints;
        }

        var admin = endpoints.MapGroup("/api/v{version:apiVersion}/admin/field-workflows/submissions")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "FieldWorkflows")
            .RequireAdminAuthorization();

        admin.MapGet("", HandleList)
            .WithName("ListFieldSubmissions")
            .WithSummary("List and filter mobile-submitted field records for review.")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]))
            .Produces<FieldSubmissionListResult>();

        admin.MapGet("/{submissionId:guid}", HandleGet)
            .WithName("GetFieldSubmission")
            .WithSummary("Get a submitted field record with review state, comments, and attachments.")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]))
            .Produces<FieldSubmissionDetail>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        admin.MapPost("/{submissionId:guid}/assignment", HandleAssign)
            .WithName("AssignFieldSubmission")
            .WithSummary("Assign or unassign a submitted record to a reviewer.")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]))
            .Produces<FieldReviewState>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        admin.MapPost("/{submissionId:guid}/decision", HandleDecide)
            .WithName("DecideFieldSubmission")
            .WithSummary("Approve, reject, or request changes on a submitted record (audited, If-Match aware).")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]))
            .Produces<FieldReviewState>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        admin.MapPost("/{submissionId:guid}/comments", HandleAddComment)
            .WithName("CommentFieldSubmission")
            .WithSummary("Add a reviewer comment or correction request to a submitted record.")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]))
            .Produces<FieldReviewComment>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static Task<IResult> HandleList(
        HttpContext context,
        [FromServices] FieldReviewService service)
        => service.ListAsync(context);

    private static Task<IResult> HandleGet(
        HttpContext context,
        [FromServices] FieldReviewService service,
        Guid submissionId)
        => service.GetAsync(context, submissionId);

    private static Task<IResult> HandleAssign(
        HttpContext context,
        [FromServices] FieldReviewService service,
        Guid submissionId)
        => service.AssignAsync(context, submissionId);

    private static Task<IResult> HandleDecide(
        HttpContext context,
        [FromServices] FieldReviewService service,
        Guid submissionId)
        => service.DecideAsync(context, submissionId);

    private static Task<IResult> HandleAddComment(
        HttpContext context,
        [FromServices] FieldReviewService service,
        Guid submissionId)
        => service.AddCommentAsync(context, submissionId);
}
