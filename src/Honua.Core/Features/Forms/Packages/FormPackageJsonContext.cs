// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Forms.Packages;

/// <summary>
/// Source-generated JSON context for form package contracts.
/// </summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.General,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FormPackageDocument))]
[JsonSerializable(typeof(FormTargetDefinition))]
[JsonSerializable(typeof(FormSectionDefinition))]
[JsonSerializable(typeof(FormFieldDefinition))]
[JsonSerializable(typeof(FormFieldDomainDefinition))]
[JsonSerializable(typeof(FormDomainChoice))]
[JsonSerializable(typeof(FormValidationRule))]
[JsonSerializable(typeof(FormConditionalRule))]
[JsonSerializable(typeof(FormSubmitPolicy))]
[JsonSerializable(typeof(FormAttachmentPolicy))]
[JsonSerializable(typeof(FormFieldAttachmentPolicy))]
[JsonSerializable(typeof(FormPrivacyPolicy))]
[JsonSerializable(typeof(FormOfflinePolicy))]
[JsonSerializable(typeof(FormProvenanceRef))]
[JsonSerializable(typeof(FormPackageVersion))]
[JsonSerializable(typeof(FormPackageVersion[]))]
[JsonSerializable(typeof(FormPackageSummary))]
[JsonSerializable(typeof(FormPackageSummary[]))]
[JsonSerializable(typeof(FormPackageValidationResult))]
[JsonSerializable(typeof(FormValidationIssue))]
[JsonSerializable(typeof(FormValidationIssue[]))]
[JsonSerializable(typeof(FormSubmissionRequest))]
[JsonSerializable(typeof(FormSubmissionAttachmentDescriptor))]
[JsonSerializable(typeof(FormSubmissionAttachmentDescriptor[]))]
[JsonSerializable(typeof(FormSubmissionResponse))]
[JsonSerializable(typeof(FormEditOutcome))]
[JsonSerializable(typeof(FormSubmissionAttachmentOutcome))]
[JsonSerializable(typeof(FormSubmissionAttachmentOutcome[]))]
[JsonSerializable(typeof(FormSubmissionRetryGuidance))]
[JsonSerializable(typeof(FormOfflinePolicyResponse))]
[JsonSerializable(typeof(FormLink))]
[JsonSerializable(typeof(FormLink[]))]
[JsonSerializable(typeof(FormSubmissionRecord))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class FormPackageJsonContext : JsonSerializerContext
{
}
