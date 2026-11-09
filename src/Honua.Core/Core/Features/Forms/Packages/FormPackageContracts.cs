// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Forms.Packages;

public static class FormPackageStatus
{
    public const string Draft = "draft";

    public const string Published = "published";

    public const string Archived = "archived";
}

public static class FormSubmissionOperations
{
    public const string Create = "create";

    public const string Update = "update";

    public const string Delete = "delete";
}

public sealed class FormPackageDocument
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = "honua.form-package.v1";

    [JsonPropertyName("formId")]
    public string? FormId { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("target")]
    public FormTargetDefinition? Target { get; init; }

    [JsonPropertyName("sections")]
    public FormSectionDefinition[] Sections { get; init => field = value ?? []; } = [];

    [JsonPropertyName("fields")]
    public FormFieldDefinition[] Fields { get; init => field = value ?? []; } = [];

    [JsonPropertyName("submitPolicy")]
    public FormSubmitPolicy SubmitPolicy { get; init => field = value ?? new(); } = new();

    [JsonPropertyName("attachmentPolicy")]
    public FormAttachmentPolicy AttachmentPolicy { get; init => field = value ?? new(); } = new();

    [JsonPropertyName("privacyPolicy")]
    public FormPrivacyPolicy PrivacyPolicy { get; init => field = value ?? new(); } = new();

    [JsonPropertyName("offlinePolicy")]
    public FormOfflinePolicy OfflinePolicy { get; init => field = value ?? new(); } = new();

    [JsonPropertyName("provenance")]
    public FormProvenanceRef? Provenance { get; init; }

    // Package metadata values must be policy-safe and must not be secrets.
    [JsonPropertyName("metadata")]
    public Dictionary<string, string>? Metadata { get; init; }
}

public sealed class FormTargetDefinition
{
    [JsonPropertyName("serviceId")]
    public string? ServiceId { get; init; }

    [JsonPropertyName("layerId")]
    public int LayerId { get; init; }
}

public sealed class FormSectionDefinition
{
    [JsonPropertyName("sectionId")]
    public string? SectionId { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("fieldIds")]
    public string[] FieldIds { get; init => field = value ?? []; } = [];

    [JsonPropertyName("repeatable")]
    public bool Repeatable { get; init; }

    [JsonPropertyName("minInstances")]
    public int? MinInstances { get; init; }

    [JsonPropertyName("maxInstances")]
    public int? MaxInstances { get; init; }
}

public sealed class FormFieldDefinition
{
    [JsonPropertyName("fieldId")]
    public string? FieldId { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }

    [JsonPropertyName("hint")]
    public string? Hint { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("targetField")]
    public string? TargetField { get; init; }

    [JsonPropertyName("sectionId")]
    public string? SectionId { get; init; }

    [JsonPropertyName("required")]
    public bool Required { get; init; }

    [JsonPropertyName("readOnly")]
    public bool ReadOnly { get; init; }

    [JsonPropertyName("private")]
    public bool Private { get; init; }

    [JsonPropertyName("defaultValue")]
    public JsonElement? DefaultValue { get; init; }

    [JsonPropertyName("domain")]
    public FormFieldDomainDefinition? Domain { get; init; }

    [JsonPropertyName("validation")]
    public FormValidationRule[] Validation { get; init => field = value ?? []; } = [];

    [JsonPropertyName("visibility")]
    public FormConditionalRule? Visibility { get; init; }
}

public sealed class FormFieldDomainDefinition
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("choices")]
    public FormDomainChoice[] Choices { get; init => field = value ?? []; } = [];

    [JsonPropertyName("min")]
    public JsonElement? Min { get; init; }

    [JsonPropertyName("max")]
    public JsonElement? Max { get; init; }
}

public sealed class FormDomainChoice
{
    [JsonPropertyName("code")]
    public JsonElement Code { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }
}

public sealed class FormValidationRule
{
    [JsonPropertyName("ruleId")]
    public string? RuleId { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("parameters")]
    public JsonElement? Parameters { get; init; }
}

public sealed class FormConditionalRule
{
    [JsonPropertyName("dependsOnFieldId")]
    public string? DependsOnFieldId { get; init; }

    [JsonPropertyName("operator")]
    public string? Operator { get; init; }

    [JsonPropertyName("value")]
    public JsonElement? Value { get; init; }
}

public sealed class FormSubmitPolicy
{
    [JsonPropertyName("allowedOperations")]
    public string[] AllowedOperations { get; init => field = value ?? []; } = [FormSubmissionOperations.Create];

    [JsonPropertyName("requiresGeometry")]
    public bool RequiresGeometry { get; init; } = true;

    [JsonPropertyName("allowAttachments")]
    public bool AllowAttachments { get; init; } = true;

    [JsonPropertyName("maxOfflineAgeSeconds")]
    public int? MaxOfflineAgeSeconds { get; init; }
}

public sealed class FormAttachmentPolicy
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("maxAttachmentsPerSubmission")]
    public int? MaxAttachmentsPerSubmission { get; init; }

    [JsonPropertyName("maxAttachmentBytes")]
    public long? MaxAttachmentBytes { get; init; }

    [JsonPropertyName("maxTotalBytes")]
    public long? MaxTotalBytes { get; init; }

    [JsonPropertyName("allowedContentTypes")]
    public string[] AllowedContentTypes { get; init => field = value ?? []; } = [];

    [JsonPropertyName("fields")]
    public FormFieldAttachmentPolicy[] Fields { get; init => field = value ?? []; } = [];

    [JsonPropertyName("requireExifStripping")]
    public bool RequireExifStripping { get; init; }

    [JsonPropertyName("requireFaceBlur")]
    public bool RequireFaceBlur { get; init; }

    [JsonPropertyName("requireRedaction")]
    public bool RequireRedaction { get; init; }
}

public sealed class FormFieldAttachmentPolicy
{
    [JsonPropertyName("fieldId")]
    public string? FieldId { get; init; }

    [JsonPropertyName("maxCount")]
    public int? MaxCount { get; init; }

    [JsonPropertyName("allowedContentTypes")]
    public string[] AllowedContentTypes { get; init => field = value ?? []; } = [];

    [JsonPropertyName("required")]
    public bool Required { get; init; }
}

public sealed class FormPrivacyPolicy
{
    [JsonPropertyName("privateFieldIds")]
    public string[] PrivateFieldIds { get; init => field = value ?? []; } = [];

    [JsonPropertyName("requiredTransformations")]
    public string[] RequiredTransformations { get; init => field = value ?? []; } = [];

    [JsonPropertyName("captureActor")]
    public bool CaptureActor { get; init; } = true;

    [JsonPropertyName("captureDeviceId")]
    public bool CaptureDeviceId { get; init; } = true;

    [JsonPropertyName("retentionDays")]
    public int? RetentionDays { get; init; }
}

public sealed class FormOfflinePolicy
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("preferredTransports")]
    public string[] PreferredTransports { get; init => field = value ?? []; } = ["feature-server-replica", "fieldcollection"];

    [JsonPropertyName("replicaTransportEnabled")]
    public bool ReplicaTransportEnabled { get; init; } = true;

    [JsonPropertyName("fieldCollectionTransportEnabled")]
    public bool FieldCollectionTransportEnabled { get; init; } = true;

    [JsonPropertyName("conflictReviewMode")]
    public string? ConflictReviewMode { get; init; } = "defer";
}

public sealed class FormProvenanceRef
{
    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("sourceVersion")]
    public string? SourceVersion { get; init; }
}

public sealed class FormPackageVersion
{
    [JsonPropertyName("formId")]
    public string FormId { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public int Version { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = FormPackageStatus.Draft;

    [JsonPropertyName("package")]
    public FormPackageDocument Package { get; init; } = new();

    [JsonPropertyName("validation")]
    public FormPackageValidationResult? Validation { get; init; }

    [JsonPropertyName("contentHash")]
    public string ContentHash { get; init; } = string.Empty;

    [JsonPropertyName("policyHash")]
    public string PolicyHash { get; init; } = string.Empty;

    [JsonPropertyName("etag")]
    public string ETag { get; init; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("createdBy")]
    public string? CreatedBy { get; init; }

    [JsonPropertyName("publishedAt")]
    public DateTimeOffset? PublishedAt { get; init; }

    [JsonPropertyName("publishedBy")]
    public string? PublishedBy { get; init; }

    [JsonPropertyName("reopenedFromVersion")]
    public int? ReopenedFromVersion { get; init; }
}

public sealed class FormPackageSummary
{
    [JsonPropertyName("formId")]
    public string FormId { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("serviceId")]
    public string ServiceId { get; init; } = string.Empty;

    [JsonPropertyName("layerId")]
    public int LayerId { get; init; }

    [JsonPropertyName("currentDraftVersion")]
    public int? CurrentDraftVersion { get; init; }

    [JsonPropertyName("currentPublishedVersion")]
    public int? CurrentPublishedVersion { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class FormPackageValidationResult
{
    [JsonPropertyName("isValid")]
    public bool IsValid { get; init; }

    [JsonPropertyName("issues")]
    public FormValidationIssue[] Issues { get; init; } = [];
}

public sealed class FormValidationIssue
{
    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    [JsonPropertyName("severity")]
    public string Severity { get; init; } = "error";

    [JsonPropertyName("fieldId")]
    public string? FieldId { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
}

public sealed class FormSubmissionRequest
{
    [JsonPropertyName("idempotencyKey")]
    public string? IdempotencyKey { get; init; }

    [JsonPropertyName("formVersion")]
    public int? FormVersion { get; init; }

    [JsonPropertyName("operation")]
    public string Operation { get; init; } = FormSubmissionOperations.Create;

    [JsonPropertyName("targetFeatureId")]
    public long? TargetFeatureId { get; init; }

    [JsonPropertyName("clientId")]
    public string? ClientId { get; init; }

    [JsonPropertyName("submittedAt")]
    public DateTimeOffset? SubmittedAt { get; init; }

    [JsonPropertyName("values")]
    public Dictionary<string, JsonElement> Values { get; init => field = value ?? new(StringComparer.Ordinal); } = new(StringComparer.Ordinal);

    [JsonPropertyName("geometry")]
    public JsonElement? Geometry { get; init; }

    [JsonPropertyName("attachments")]
    public FormSubmissionAttachmentDescriptor[] Attachments { get; init => field = value ?? []; } = [];
}

public sealed class FormSubmissionAttachmentDescriptor
{
    [JsonPropertyName("clientAttachmentId")]
    public string? ClientAttachmentId { get; init; }

    [JsonPropertyName("fieldId")]
    public string? FieldId { get; init; }

    [JsonPropertyName("partName")]
    public string? PartName { get; init; }

    [JsonPropertyName("filename")]
    public string? Filename { get; init; }

    [JsonPropertyName("contentType")]
    public string? ContentType { get; init; }

    [JsonPropertyName("sizeBytes")]
    public long? SizeBytes { get; init; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; init; }
}

public sealed class FormSubmissionResponse
{
    [JsonPropertyName("submissionId")]
    public Guid SubmissionId { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "accepted";

    [JsonPropertyName("formId")]
    public string FormId { get; init; } = string.Empty;

    [JsonPropertyName("formVersion")]
    public int FormVersion { get; init; }

    [JsonPropertyName("operation")]
    public string Operation { get; init; } = FormSubmissionOperations.Create;

    [JsonPropertyName("targetFeatureId")]
    public long? TargetFeatureId { get; init; }

    [JsonPropertyName("editOutcome")]
    public FormEditOutcome? EditOutcome { get; init; }

    [JsonPropertyName("attachmentOutcomes")]
    public FormSubmissionAttachmentOutcome[] AttachmentOutcomes { get; init; } = [];

    [JsonPropertyName("validationIssues")]
    public FormValidationIssue[] ValidationIssues { get; init; } = [];

    [JsonPropertyName("retry")]
    public FormSubmissionRetryGuidance? Retry { get; init; }

    [JsonPropertyName("idempotentReplay")]
    public bool IdempotentReplay { get; init; }
}

public sealed class FormEditOutcome
{
    [JsonPropertyName("succeeded")]
    public bool Succeeded { get; init; }

    [JsonPropertyName("created")]
    public int Created { get; init; }

    [JsonPropertyName("updated")]
    public int Updated { get; init; }

    [JsonPropertyName("deleted")]
    public int Deleted { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

public sealed class FormSubmissionAttachmentOutcome
{
    [JsonPropertyName("clientAttachmentId")]
    public string? ClientAttachmentId { get; init; }

    [JsonPropertyName("fieldId")]
    public string? FieldId { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "accepted";

    [JsonPropertyName("attachmentId")]
    public long? AttachmentId { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("privacyApplied")]
    public bool PrivacyApplied { get; init; }
}

public sealed class FormSubmissionRetryGuidance
{
    [JsonPropertyName("retryable")]
    public bool Retryable { get; init; }

    [JsonPropertyName("retryAfterSeconds")]
    public int? RetryAfterSeconds { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed class FormOfflinePolicyResponse
{
    [JsonPropertyName("formId")]
    public string FormId { get; init; } = string.Empty;

    [JsonPropertyName("formVersion")]
    public int FormVersion { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("serviceId")]
    public string ServiceId { get; init; } = string.Empty;

    [JsonPropertyName("layerId")]
    public int LayerId { get; init; }

    [JsonPropertyName("availableTransports")]
    public string[] AvailableTransports { get; init; } = [];

    [JsonPropertyName("links")]
    public FormLink[] Links { get; init; } = [];

    [JsonPropertyName("requiredHeaders")]
    public Dictionary<string, string> RequiredHeaders { get; init; } = new(StringComparer.Ordinal);
}

public sealed class FormLink
{
    [JsonPropertyName("rel")]
    public string Rel { get; init; } = string.Empty;

    [JsonPropertyName("href")]
    public string Href { get; init; } = string.Empty;

    [JsonPropertyName("method")]
    public string Method { get; init; } = "GET";
}

public sealed class FormSubmissionRecord
{
    public Guid SubmissionId { get; init; }

    public string RequestHash { get; init; } = string.Empty;

    public FormSubmissionResponse? Response { get; init; }

    public string Status { get; init; } = string.Empty;
}
