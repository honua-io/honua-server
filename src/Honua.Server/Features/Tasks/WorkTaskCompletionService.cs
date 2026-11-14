// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Assets;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Assets;

namespace Honua.Server.Features.Tasks;

internal sealed class WorkTaskCompletionService(
    IWorkTaskStore tasks,
    IConfigurationGenerationSource generation,
    WorkTaskCompletionProcessor processor)
{
    public async Task<IResult> CompleteAsync(HttpContext context, Guid taskId)
    {
        var request = await AssetCommandHttp.ReadAsync(context, AssetsJsonContext.Default.WorkTaskCompletionRequest).ConfigureAwait(false);
        if (request is not { CommandId: Guid commandId, ExpectedVersion: long })
        {
            return AssetCommandHttp.Rejected(context, "expectedVersion", "A JSON body with commandId and expectedVersion is required.");
        }

        var configurationGeneration = await generation.GetGenerationAsync(context.RequestAborted).ConfigureAwait(false);
        var task = await tasks.GetAsync(taskId, context.RequestAborted).ConfigureAwait(false);
        if (task is null ||
            (await AssetServiceAccess.RequireEditorAsync(context, task.ServiceId).ConfigureAwait(false)).Denied is not null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound, "The task was not found.");
        }

        var command = AssetCommandHttp.CreateCommand(
            context,
            new CommandSubject(commandId, task.ServiceId, WorkTaskAction.Complete, taskId),
            (request, AssetsJsonContext.Default.WorkTaskCompletionRequest));
        if (await tasks.FindReceiptAsync(command, context.RequestAborted).ConfigureAwait(false) is { } replay)
        {
            return AssetCommandHttp.ToResult(context, replay, StatusCodes.Status200OK);
        }

        var result = await processor.CompleteAsync(context, (task, request, command, configurationGeneration)).ConfigureAwait(false);
        if (result is IStatusCodeHttpResult { StatusCode: StatusCodes.Status400BadRequest } &&
            await tasks.FindReceiptAsync(command, context.RequestAborted).ConfigureAwait(false) is { } committed)
        {
            return AssetCommandHttp.ToResult(context, committed, StatusCodes.Status200OK);
        }

        return result;
    }
}
