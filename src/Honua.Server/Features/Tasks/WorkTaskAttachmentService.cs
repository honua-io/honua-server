// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Assets;
using Honua.Core.Features.Forms.Packages;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Security;
using Honua.Server.Features.Assets;

namespace Honua.Server.Features.Tasks;

internal sealed class WorkTaskAttachmentService(WorkTaskAttachmentAccess access, WorkTaskAttachmentContent content, FormPackageValidator validator)
{
    public async Task<IResult> UploadAsync(HttpContext context, Guid taskId)
    {
        AssetCommandHttp.ApplyNoStore(context.Response);
        var generation = await access.GetGenerationAsync(context.RequestAborted).ConfigureAwait(false);
        var task = await access.FindTaskAsync(context, taskId).ConfigureAwait(false);
        if (task is null)
        {
            return NotFound(context);
        }
        if (WorkTaskStateMachine.CheckPrecondition(task, WorkTaskAction.Complete, AssetCommandHttp.ResolveTaskActor(context)) is not null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status403Forbidden, "The caller cannot upload evidence for this task.");
        }
        var (plan, error) = await PrepareAsync(context, (task, generation)).ConfigureAwait(false);
        if (plan is null)
        {
            return AssetCommandHttp.Rejected(context, "attachments", error!);
        }
        if (!await content.UploadAsync((plan.Attachment, plan.Limits, plan.File), context.RequestAborted).ConfigureAwait(false))
        {
            return AssetCommandHttp.Rejected(context, "attachments", "The upload could not be accepted. Check the task and attachment limits before retrying.");
        }
        return Results.Json(new WorkTaskAttachmentResponse(plan.Attachment.AttachmentId),
            AssetsJsonContext.Default.WorkTaskAttachmentResponse, statusCode: StatusCodes.Status201Created);
    }

    private async Task<(UploadPlan? Plan, string? Error)> PrepareAsync(HttpContext context, (WorkTask Task, long Generation) authorization)
    {
        var task = authorization.Task;
        var (file, field) = await content.Policy.ReadAsync(context).ConfigureAwait(false);
        if (file is null || string.IsNullOrWhiteSpace(field) ||
            !(await content.Policy.ValidateAsync(file, context.RequestAborted).ConfigureAwait(false)).IsValid)
        {
            return (null, "A safe nonempty file and a single form fieldId are required within the upload size limit.");
        }
        var form = await access.FindFormAsync(task, context.RequestAborted).ConfigureAwait(false);
        if (form is null)
        {
            return (null, "The task's pinned form is unavailable.");
        }
        var attachment = CreateAttachment(context, (task, file, field));
        var descriptor = new FormSubmissionAttachmentDescriptor
        {
            FieldId = field, PartName = "file", Filename = attachment.Filename,
            ContentType = attachment.ContentType, SizeBytes = attachment.SizeBytes,
        };
        var validation = await validator.ValidateAttachmentUploadAsync(form, descriptor, context.RequestAborted).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return (null, "The upload does not satisfy the task's pinned form attachment policy.");
        }
        var limits = content.Policy.ReservationLimits(form.Package.AttachmentPolicy, field, (AssetCommandHttp.ResolveTaskActor(context), authorization.Generation));
        return (new UploadPlan(attachment, file, limits), null);
    }

    public async Task<IResult> DownloadAsync(HttpContext context, (Guid TaskId, Guid AttachmentId) ids)
    {
        AssetCommandHttp.ApplyNoStore(context.Response);
        if (await access.FindTaskAsync(context, ids.TaskId).ConfigureAwait(false) is null)
        {
            return NotFound(context);
        }
        var download = await content.DownloadAsync((ids.TaskId, ids.AttachmentId, AssetCommandHttp.ResolveActor(context)), context.RequestAborted).ConfigureAwait(false);
        if (download is null)
        {
            return NotFound(context);
        }
        context.Response.Headers.XContentTypeOptions = "nosniff";
        return Results.File(download.Value.Bytes, download.Value.Attachment.ContentType,
            fileDownloadName: download.Value.Attachment.Filename, enableRangeProcessing: false);
    }

    private static WorkTaskAttachment CreateAttachment(HttpContext context, (WorkTask Task, IFormFile File, string Field) upload)
    {
        var id = Guid.NewGuid();
        return new WorkTaskAttachment
        {
            AttachmentId = id, TaskId = upload.Task.TaskId, OwnerSubject = AssetCommandHttp.ResolveActor(context),
            FieldId = upload.Field, Filename = FileUploadSecurity.SanitizeFileName(upload.File.FileName),
            ContentType = upload.File.ContentType.Split(';', 2)[0].Trim(), SizeBytes = upload.File.Length,
            BlobKey = $"task-evidence/{upload.Task.TaskId:D}/{id:D}", ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
        };
    }

    private static IResult NotFound(HttpContext context)
        => ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound, "The task attachment was not found.");

    private sealed record UploadPlan(WorkTaskAttachment Attachment, IFormFile File, WorkTaskAttachmentLimits Limits);
}
