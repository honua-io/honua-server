// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Forms.Packages;

public sealed partial class FormPackageValidator
{
    public async Task<FormPackageValidationResult> ValidateAttachmentUploadAsync(
        FormPackageVersion packageVersion, FormSubmissionAttachmentDescriptor attachment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packageVersion);
        ArgumentNullException.ThrowIfNull(attachment);
        var issues = new List<FormValidationIssue>();
        if (packageVersion.Status is not (FormPackageStatus.Published or FormPackageStatus.Archived))
        {
            AddError(issues, "packageNotPublished", "A published form version is required for uploads.");
        }

        var package = packageVersion.Package;
        if (package.AttachmentPolicy.RequireExifStripping || package.AttachmentPolicy.RequireFaceBlur ||
            package.AttachmentPolicy.RequireRedaction || package.PrivacyPolicy.RequiredTransformations.Length > 0)
        {
            AddError(issues, "attachmentTransformUnavailable", "Required attachment transformations are unavailable.");
        }

        var target = await ResolveTargetAsync(package, issues, cancellationToken).ConfigureAwait(false);
        if (target.Resource is not null)
        {
            var request = new FormSubmissionRequest { Attachments = [attachment] };
            ValidateSubmittedAttachments(package, (request, target.Resource, false), issues);
        }
        return CreateResult(issues);
    }
}
