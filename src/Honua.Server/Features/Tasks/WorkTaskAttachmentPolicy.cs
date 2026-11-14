// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Assets;
using Honua.Core.Features.Forms.Packages;
using Honua.Infrastructure.Security;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Tasks;

internal sealed class WorkTaskAttachmentPolicy(IOptions<LimitsOptions> limits)
{
    // Every accepted byte must fit in the shared security scanner's inspection window.
    public long MaxBytes => Math.Min(limits.Value.Attachments.MaxAttachmentSize, FileUploadSecurity.MaxSecurityScanSize);

    public async Task<(IFormFile? File, string Field)> ReadAsync(HttpContext context)
    {
        var bodyLimit = MaxBytes + 65536;
        if (!context.Request.HasFormContentType || context.Request.ContentLength > bodyLimit)
        {
            return (null, "");
        }
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } body)
        {
            body.MaxRequestBodySize = bodyLimit;
        }
        context.Features.Set<IFormFeature>(new FormFeature(context.Request, new FormOptions
        {
            // Multipart limits count the file section as well as the fieldId section.
            MultipartBodyLengthLimit = MaxBytes, ValueLengthLimit = 256, ValueCountLimit = 2,
            MultipartHeadersCountLimit = 16, MultipartHeadersLengthLimit = 4096,
        }));
        try
        {
            var form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
            return form.Count == 1 && form.Files.Count == 1 && form["fieldId"].Count == 1
                ? (form.Files[0], form["fieldId"].ToString()) : (null, "");
        }
        catch (InvalidDataException) { return (null, ""); }
        catch (BadHttpRequestException) { return (null, ""); }
    }

    public Task<FileValidationResult> ValidateAsync(IFormFile file, CancellationToken cancellationToken)
        => FileUploadSecurity.ValidateFileAsync(file, MaxBytes, cancellationToken);

    public WorkTaskAttachmentLimits ReservationLimits(FormAttachmentPolicy policy, string fieldId, (WorkTaskActor Actor, long Generation) authorization)
    {
        var maximum = Math.Min(policy.MaxAttachmentsPerSubmission ?? limits.Value.Attachments.MaxAttachmentsPerFeature,
            limits.Value.Attachments.MaxAttachmentsPerFeature);
        return new WorkTaskAttachmentLimits
        {
            ConfigurationGeneration = authorization.Generation, IsPlanner = authorization.Actor.IsPlanner, MaxCount = maximum,
            MaxFieldCount = Math.Min(policy.Fields.FirstOrDefault(field =>
                string.Equals(field.FieldId, fieldId, StringComparison.OrdinalIgnoreCase))?.MaxCount ?? maximum, maximum),
            MaxTotalBytes = Math.Min(policy.MaxTotalBytes ?? limits.Value.Attachments.MaxTotalAttachmentSize,
                limits.Value.Attachments.MaxTotalAttachmentSize),
        };
    }
}
