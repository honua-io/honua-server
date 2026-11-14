// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Assets;
using Honua.Infrastructure.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Tasks;

internal static partial class WorkTaskEndpoints
{

    public static IEndpointRouteBuilder MapWorkTaskEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var serviceInspector = endpoints.ServiceProvider.GetService<IServiceProviderIsService>();
        if (serviceInspector is not null && !serviceInspector.IsService(typeof(IWorkTaskStore)))
        {
            return endpoints;
        }

        // Crew are service data editors, not admins: the group admits any authenticated principal by any configured sign-in
        // (never anonymous, even on services that allow anonymous feature writes), each handler applies the shared service
        // data-editor check, and planner-only actions require an administrator (AssetCommandHttp.ResolveTaskActor).
        var tasks = endpoints.MapGroup("/api/v{version:apiVersion}/tasks")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Tasks")
            .RequireAuthenticatedAuthorization();

        tasks.MapGet("", HandleList)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .WithName("ListWorkTasks")
            .WithSummary("List the tasks of a service, filtered by status, assignee or type.")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]))
            .Produces<WorkTaskListResult>()
            .Produces<AssetValidationProblem>(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        tasks.MapPost("", HandleCreate)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .WithName("CreateWorkTask")
            .WithSummary("Create a task with a client-generated ID (idempotent by commandId).")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]))
            .Accepts<WorkTaskCreateRequest>("application/json")
            .Produces<WorkTaskCommandResponse>(StatusCodes.Status201Created)
            .Produces<AssetValidationProblem>(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        tasks.MapGet("/{taskId:guid}", HandleGet)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .WithName("GetWorkTask")
            .WithSummary("Get a task with the actions the caller may take.")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]))
            .Produces<WorkTask>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        tasks.MapPost("/{taskId:guid}/transitions", HandleTransition)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .WithName("TransitionWorkTask")
            .WithSummary("Assign, unassign, start, reassign or cancel a task at an expected version (idempotent by commandId).")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]))
            .Accepts<WorkTaskTransitionRequest>("application/json")
            .Produces<WorkTaskCommandResponse>()
            .Produces<AssetValidationProblem>(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces<AssetCommandConflict>(StatusCodes.Status409Conflict);

        tasks.MapPost("/{taskId:guid}/completion", HandleComplete)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .WithName("CompleteWorkTask")
            .WithSummary("Complete an in-progress task: records the inspection or service record and advances the asset summary atomically.")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]))
            .Accepts<WorkTaskCompletionRequest>("application/json")
            .Produces<WorkTaskCommandResponse>()
            .Produces<AssetValidationProblem>(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces<AssetCommandConflict>(StatusCodes.Status409Conflict);

        tasks.MapGet("/{taskId:guid}/history", HandleHistory)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .WithName("ListWorkTaskHistory")
            .WithSummary("List a task's immutable history, oldest first.")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]))
            .Produces<WorkTaskHistoryResult>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        MapAttachmentEndpoints(tasks);
        return endpoints;
    }

    private static Task<IResult> HandleList(HttpContext context, [FromServices] WorkTaskService service)
        => service.ListAsync(context);

    private static Task<IResult> HandleCreate(HttpContext context, [FromServices] WorkTaskService service)
        => service.CreateAsync(context);

    private static Task<IResult> HandleGet(HttpContext context, [FromServices] WorkTaskService service, Guid taskId)
        => service.GetAsync(context, taskId);

    private static Task<IResult> HandleTransition(HttpContext context, [FromServices] WorkTaskService service, Guid taskId)
        => service.TransitionAsync(context, taskId);

    private static Task<IResult> HandleComplete(HttpContext context, [FromServices] WorkTaskCompletionService service, Guid taskId)
        => service.CompleteAsync(context, taskId);

    private static Task<IResult> HandleHistory(HttpContext context, [FromServices] WorkTaskService service, Guid taskId)
        => service.HistoryAsync(context, taskId);
}
