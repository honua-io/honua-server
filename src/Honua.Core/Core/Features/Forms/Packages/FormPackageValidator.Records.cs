// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Core.Features.Forms.Packages;

public sealed partial class FormPackageValidator
{
    public async Task<FormPackageValidationResult> ValidateRecordAsync(
        FormPackageVersion packageVersion,
        (Dictionary<string, JsonElement> Values, FormSubmissionAttachmentDescriptor[] Attachments) record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packageVersion);
        var (values, attachments) = record;
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(attachments);

        var issues = new List<FormValidationIssue>();
        // Archived published versions remain valid pins; drafts have never been published.
        if (!string.Equals(packageVersion.Status, FormPackageStatus.Published, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(packageVersion.Status, FormPackageStatus.Archived, StringComparison.OrdinalIgnoreCase))
        {
            AddError(issues, "packageNotPublished", "A published form version is required for evidence.");
        }

        var package = packageVersion.Package;
        var target = await ResolveTargetAsync(package, issues, cancellationToken).ConfigureAwait(false);
        if (target.Resource is not null)
        {
            var request = new FormSubmissionRequest { Values = values, Attachments = attachments };
            ValidateSubmittedValues(package, request, target.Resource, issues);
            ValidateSubmittedAttachments(package, (request, target.Resource, true), issues);
        }

        return CreateResult(issues);
    }
}
