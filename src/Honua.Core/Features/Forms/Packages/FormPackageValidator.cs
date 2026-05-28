// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.Metadata.Domain.V2;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.Forms.Packages;

/// <summary>
/// Validates form packages and submissions against target layer schema and server policy.
/// </summary>
public sealed class FormPackageValidator
{
    private static readonly HashSet<string> _supportedVisibilityOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        "equals",
        "notEquals",
        "gt",
        "gte",
        "lt",
        "lte",
        "isEmpty",
        "isNotEmpty",
        "in"
    };

    private static readonly HashSet<string> _allowedPrivacyTransforms = new(StringComparer.OrdinalIgnoreCase)
    {
        "none",
        "auditOnly",
        "minimizeAudit"
    };

    private readonly IFormTargetMetadataResolver _targetResolver;
    private readonly AttachmentLimits _attachmentLimits;

    /// <summary>
    /// Initializes a new validator.
    /// </summary>
    public FormPackageValidator(IFormTargetMetadataResolver targetResolver, IOptions<LimitsOptions> limitsOptions)
    {
        _targetResolver = targetResolver ?? throw new ArgumentNullException(nameof(targetResolver));
        ArgumentNullException.ThrowIfNull(limitsOptions);
        _attachmentLimits = limitsOptions.Value.Attachments;
    }

    /// <summary>
    /// Validates a package before publishing.
    /// </summary>
    public async Task<FormPackageValidationResult> ValidateForPublishAsync(
        FormPackageDocument package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);

        var issues = new List<FormValidationIssue>();
        ValidatePackageShape(package, issues);

        var target = await ResolveTargetAsync(package, issues, cancellationToken).ConfigureAwait(false);
        if (target is { Service: not null, Publication: not null, Resource: not null })
        {
            ValidateSubmitPolicy(package, target.Service, target.Publication, target.Resource, issues);
            ValidateFields(package, target.Resource, issues);
            ValidateSections(package, issues);
            ValidateAttachmentPolicy(package, target.Resource, issues);
            ValidatePrivacyPolicy(package, issues);
            ValidateOfflinePolicy(package, issues);
        }

        return CreateResult(issues);
    }

    /// <summary>
    /// Validates a runtime submission against the published package and target layer.
    /// </summary>
    public async Task<FormPackageValidationResult> ValidateSubmissionAsync(
        FormPackageVersion packageVersion,
        FormSubmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packageVersion);
        ArgumentNullException.ThrowIfNull(request);

        var issues = new List<FormValidationIssue>();
        if (!string.Equals(packageVersion.Status, FormPackageStatus.Published, StringComparison.OrdinalIgnoreCase))
        {
            AddError(issues, "packageNotPublished", "Published package version is required for submissions.");
        }

        var package = packageVersion.Package;
        var target = await ResolveTargetAsync(package, issues, cancellationToken).ConfigureAwait(false);
        if (target.Resource is null)
        {
            return CreateResult(issues);
        }

        ValidateSubmissionPolicy(package, request, target.Resource, issues);
        ValidateSubmittedValues(package, request, target.Resource, issues);
        ValidateSubmittedGeometry(package, request, target.Resource, issues);
        ValidateSubmittedAttachments(package, request, target.Resource, issues);

        return CreateResult(issues);
    }

    /// <summary>
    /// Resolves the target service, publication, and resource for a package.
    /// </summary>
    public async Task<FormTargetMetadataResolution> ResolveTargetAsync(
        FormPackageDocument package,
        List<FormValidationIssue> issues,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(issues);

        var target = package.Target;
        if (target is null || string.IsNullOrWhiteSpace(target.ServiceId))
        {
            AddError(issues, "targetRequired", "target.serviceId and target.layerId are required.", path: "target");
            return default;
        }

        var resolved = await _targetResolver.ResolveAsync(target, cancellationToken).ConfigureAwait(false);
        if (resolved.Service is null)
        {
            AddError(issues, "serviceNotFound", $"Target service '{target.ServiceId}' was not found.", path: "target.serviceId");
            return default;
        }

        if (resolved.Publication is null || resolved.Resource is null)
        {
            AddError(issues, "layerNotFound", $"Target layer '{target.LayerId}' was not found in service '{target.ServiceId}'.", path: "target.layerId");
            return new FormTargetMetadataResolution(resolved.Service, null, null, null);
        }

        return resolved;
    }

    private static void ValidatePackageShape(FormPackageDocument package, List<FormValidationIssue> issues)
    {
        if (!string.Equals(package.SchemaVersion, "honua.form-package.v1", StringComparison.Ordinal))
        {
            AddError(issues, "schemaVersionUnsupported", "schemaVersion must be honua.form-package.v1.", path: "schemaVersion");
        }

        if (string.IsNullOrWhiteSpace(package.Title))
        {
            AddError(issues, "titleRequired", "Package title is required.", path: "title");
        }

        if (package.Fields.Length == 0)
        {
            AddError(issues, "fieldsRequired", "At least one field is required.", path: "fields");
        }

        var duplicateFields = package.Fields
            .Where(static field => !string.IsNullOrWhiteSpace(field.FieldId))
            .GroupBy(static field => field.FieldId!, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() > 1);

        foreach (var duplicate in duplicateFields)
        {
            AddError(issues, "fieldIdDuplicate", $"Field id '{duplicate.Key}' is duplicated.", duplicate.Key, "fields");
        }
    }

    private static void ValidateSubmitPolicy(
        FormPackageDocument package,
        MetadataV2Service service,
        MetadataV2Publication publication,
        MetadataV2Resource resource,
        List<FormValidationIssue> issues)
    {
        var allowedOperations = package.SubmitPolicy.AllowedOperations;
        if (allowedOperations.Length == 0)
        {
            AddError(issues, "submitPolicyOperationRequired", "submitPolicy.allowedOperations must include at least one operation.", path: "submitPolicy.allowedOperations");
            return;
        }

        foreach (var operation in allowedOperations)
        {
            if (!IsKnownOperation(operation))
            {
                AddError(issues, "submitPolicyOperationUnsupported", $"Submit operation '{operation}' is not supported.", path: "submitPolicy.allowedOperations");
                continue;
            }

            if (!TargetSupportsOperation(service, publication, resource, operation))
            {
                AddError(
                    issues,
                    "targetOperationUnsupported",
                    $"Target service '{service.Metadata.Name}' does not advertise '{ToServiceCapability(operation)}' capability.",
                    path: "submitPolicy.allowedOperations");
            }
        }

        if (package.SubmitPolicy.RequiresGeometry && ResourceGeometryType(resource) == MetadataV2GeometryType.None)
        {
            AddWarning(issues, "geometryNotAvailable", "submitPolicy.requiresGeometry is true, but the target layer has no geometry.", path: "submitPolicy.requiresGeometry");
        }
    }

    private static void ValidateFields(FormPackageDocument package, MetadataV2Resource resource, List<FormValidationIssue> issues)
    {
        var sectionIds = package.Sections
            .Where(static section => !string.IsNullOrWhiteSpace(section.SectionId))
            .Select(static section => section.SectionId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var layerFields = resource.SchemaFields.ToDictionary(static field => field.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var field in package.Fields)
        {
            if (string.IsNullOrWhiteSpace(field.FieldId))
            {
                AddError(issues, "fieldIdRequired", "Field fieldId is required.", path: "fields.fieldId");
                continue;
            }

            if (string.IsNullOrWhiteSpace(field.Label))
            {
                AddError(issues, "fieldLabelRequired", $"Field '{field.FieldId}' requires a label.", field.FieldId);
            }

            if (!string.IsNullOrWhiteSpace(field.SectionId) && !sectionIds.Contains(field.SectionId))
            {
                AddError(issues, "sectionNotFound", $"Field '{field.FieldId}' references unknown section '{field.SectionId}'.", field.FieldId);
            }

            if (IsAttachmentField(field))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(field.TargetField))
            {
                AddError(issues, "targetFieldRequired", $"Field '{field.FieldId}' requires a targetField.", field.FieldId);
                continue;
            }

            if (!layerFields.TryGetValue(field.TargetField, out var layerField))
            {
                AddError(issues, "targetFieldNotFound", $"Target field '{field.TargetField}' was not found on layer '{resource.Metadata.Name}'.", field.FieldId);
                continue;
            }

            if (IsGeometryField(layerField) || layerField.Hidden || !layerField.Editable)
            {
                AddError(issues, "targetFieldNotWritable", $"Target field '{layerField.Name}' is not writable.", field.FieldId);
            }

            if (!IsCompatibleFieldType(field.Type, layerField.Type))
            {
                AddError(
                    issues,
                    "targetFieldTypeMismatch",
                    $"Field '{field.FieldId}' type '{field.Type}' is not compatible with target field '{layerField.Name}' type '{layerField.Type}'.",
                    field.FieldId);
            }

            if (!layerField.Nullable && !field.Required && field.DefaultValue is null)
            {
                AddError(
                    issues,
                    "requiredStateMismatch",
                    $"Target field '{layerField.Name}' is non-nullable; form field '{field.FieldId}' must be required or declare a default value.",
                    field.FieldId);
            }

            ValidateFieldDomain(field, layerField, issues);
            ValidateFieldValidationRules(field, layerField, issues);
            ValidateVisibilityRule(field, package.Fields, issues);
        }

        ValidateVisibilityCycles(package.Fields, issues);
    }

    private static void ValidateFieldDomain(
        FormFieldDefinition field,
        MetadataV2Field layerField,
        List<FormValidationIssue> issues)
    {
        if (field.Domain is null)
        {
            return;
        }

        if (string.Equals(field.Domain.Type, "codedValue", StringComparison.OrdinalIgnoreCase))
        {
            if (field.Domain.Choices.Length == 0)
            {
                AddError(issues, "domainChoicesRequired", "codedValue domain requires choices.", field.FieldId);
            }

            if (field.Domain.Choices.Any(static choice => choice.Code.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null))
            {
                AddError(issues, "domainChoiceCodeRequired", "codedValue domain choice requires a code.", field.FieldId);
            }

            var duplicateCodes = field.Domain.Choices
                .GroupBy(static choice => NormalizeJson(choice.Code), StringComparer.Ordinal)
                .Where(static group => group.Count() > 1);
            foreach (var duplicate in duplicateCodes)
            {
                AddError(issues, "domainChoiceDuplicate", $"Domain choice code '{duplicate.Key}' is duplicated.", field.FieldId);
            }

            if (layerField.Domain?.CodedValues is { Count: > 0 } layerChoices)
            {
                var allowedCodes = layerChoices
                    .Select(static value => JsonElementToString(value.Code))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var choice in field.Domain.Choices)
                {
                    var code = JsonElementToString(choice.Code);
                    if (!allowedCodes.Contains(code))
                    {
                        AddError(issues, "domainChoiceOutsideTarget", $"Domain choice '{code}' is not allowed by target field domain.", field.FieldId);
                    }
                }
            }
        }

        if (string.Equals(field.Domain.Type, "range", StringComparison.OrdinalIgnoreCase) &&
            (field.Domain.Min is null || field.Domain.Max is null))
        {
            AddError(issues, "domainRangeIncomplete", "range domain requires min and max.", field.FieldId);
        }
    }

    private static void ValidateFieldValidationRules(
        FormFieldDefinition field,
        MetadataV2Field layerField,
        List<FormValidationIssue> issues)
    {
        foreach (var rule in field.Validation)
        {
            if (string.IsNullOrWhiteSpace(rule.Type))
            {
                AddError(issues, "validationRuleTypeRequired", "Validation rule type is required.", field.FieldId);
            }

            if (string.Equals(rule.Type, "maxLength", StringComparison.OrdinalIgnoreCase) &&
                layerField.Length is int maxLayerLength &&
                TryGetNumericParameter(rule.Parameters, "value", out var maxLength) &&
                maxLength > maxLayerLength)
            {
                AddError(issues, "validationMaxLengthExceedsTarget", $"maxLength {maxLength} exceeds target field length {maxLayerLength}.", field.FieldId);
            }
        }
    }

    private static void ValidateVisibilityRule(
        FormFieldDefinition field,
        FormFieldDefinition[] fields,
        List<FormValidationIssue> issues)
    {
        if (field.Visibility is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(field.Visibility.DependsOnFieldId) ||
            !fields.Any(candidate => string.Equals(candidate.FieldId, field.Visibility.DependsOnFieldId, StringComparison.OrdinalIgnoreCase)))
        {
            AddError(issues, "visibilityFieldNotFound", $"Visibility dependency for field '{field.FieldId}' was not found.", field.FieldId);
        }

        if (string.IsNullOrWhiteSpace(field.Visibility.Operator) ||
            !_supportedVisibilityOperators.Contains(field.Visibility.Operator))
        {
            AddError(issues, "visibilityOperatorUnsupported", $"Visibility operator '{field.Visibility.Operator}' is not supported.", field.FieldId);
        }
    }

    private static void ValidateVisibilityCycles(FormFieldDefinition[] fields, List<FormValidationIssue> issues)
    {
        var dependencies = fields
            .Where(static field => !string.IsNullOrWhiteSpace(field.FieldId) &&
                !string.IsNullOrWhiteSpace(field.Visibility?.DependsOnFieldId))
            .ToDictionary(
                static field => field.FieldId!,
                static field => field.Visibility!.DependsOnFieldId!,
                StringComparer.OrdinalIgnoreCase);

        foreach (var start in dependencies.Keys)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var current = start;
            while (dependencies.TryGetValue(current, out var next))
            {
                if (!seen.Add(current))
                {
                    AddError(issues, "visibilityCycle", $"Visibility rules contain a cycle involving field '{start}'.", start);
                    break;
                }

                current = next;
            }
        }
    }

    private static void ValidateSections(FormPackageDocument package, List<FormValidationIssue> issues)
    {
        var duplicateSections = package.Sections
            .Where(static section => !string.IsNullOrWhiteSpace(section.SectionId))
            .GroupBy(static section => section.SectionId!, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() > 1);

        foreach (var duplicate in duplicateSections)
        {
            AddError(issues, "sectionIdDuplicate", $"Section id '{duplicate.Key}' is duplicated.", path: "sections");
        }

        var fieldIds = package.Fields
            .Where(static field => !string.IsNullOrWhiteSpace(field.FieldId))
            .Select(static field => field.FieldId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var section in package.Sections)
        {
            if (string.IsNullOrWhiteSpace(section.SectionId))
            {
                AddError(issues, "sectionIdRequired", "Section sectionId is required.", path: "sections.sectionId");
            }

            if (string.IsNullOrWhiteSpace(section.Label))
            {
                AddError(issues, "sectionLabelRequired", $"Section '{section.SectionId}' requires a label.", path: "sections.label");
            }

            foreach (var fieldId in section.FieldIds)
            {
                if (!fieldIds.Contains(fieldId))
                {
                    AddError(issues, "sectionFieldNotFound", $"Section '{section.SectionId}' references unknown field '{fieldId}'.", fieldId);
                }
            }
        }
    }

    private void ValidateAttachmentPolicy(FormPackageDocument package, MetadataV2Resource resource, List<FormValidationIssue> issues)
    {
        var attachmentFields = package.Fields.Where(IsAttachmentField).ToArray();
        if (!package.AttachmentPolicy.Enabled && attachmentFields.Length == 0)
        {
            return;
        }

        if (!ResourceSupportsAttachments(resource))
        {
            AddError(issues, "attachmentsNotSupported", $"Target layer '{resource.Metadata.Name}' does not support attachments.", path: "attachmentPolicy");
        }

        if (!package.SubmitPolicy.AllowAttachments)
        {
            AddError(issues, "attachmentsDisallowedBySubmitPolicy", "submitPolicy.allowAttachments must be true when attachment policy or fields are enabled.", path: "submitPolicy.allowAttachments");
        }

        var maxCount = package.AttachmentPolicy.MaxAttachmentsPerSubmission ?? _attachmentLimits.MaxAttachmentsPerFeature;
        if (maxCount <= 0 || maxCount > _attachmentLimits.MaxAttachmentsPerFeature)
        {
            AddError(issues, "attachmentCountLimitInvalid", $"Attachment max count must be between 1 and {_attachmentLimits.MaxAttachmentsPerFeature}.", path: "attachmentPolicy.maxAttachmentsPerSubmission");
        }

        var maxSize = package.AttachmentPolicy.MaxAttachmentBytes ?? _attachmentLimits.MaxAttachmentSize;
        if (maxSize <= 0 || maxSize > _attachmentLimits.MaxAttachmentSize)
        {
            AddError(issues, "attachmentSizeLimitInvalid", $"Attachment max size must be between 1 and {_attachmentLimits.MaxAttachmentSize} bytes.", path: "attachmentPolicy.maxAttachmentBytes");
        }

        var maxTotal = package.AttachmentPolicy.MaxTotalBytes ?? _attachmentLimits.MaxTotalAttachmentSize;
        if (maxTotal <= 0 || maxTotal > _attachmentLimits.MaxTotalAttachmentSize)
        {
            AddError(issues, "attachmentTotalLimitInvalid", $"Attachment total size must be between 1 and {_attachmentLimits.MaxTotalAttachmentSize} bytes.", path: "attachmentPolicy.maxTotalBytes");
        }

        foreach (var contentType in package.AttachmentPolicy.AllowedContentTypes)
        {
            if (!MimeIsAllowedByServer(contentType))
            {
                AddError(issues, "attachmentContentTypeNotAllowed", $"Attachment content type '{contentType}' is not allowed by server limits.", path: "attachmentPolicy.allowedContentTypes");
            }
        }

        if (package.AttachmentPolicy.RequireExifStripping ||
            package.AttachmentPolicy.RequireFaceBlur ||
            package.AttachmentPolicy.RequireRedaction)
        {
            AddError(issues, "attachmentTransformUnsupported", "Attachment privacy transforms must be performed before submission in this release.", path: "attachmentPolicy");
        }

        var attachmentFieldIds = attachmentFields
            .Where(static field => !string.IsNullOrWhiteSpace(field.FieldId))
            .Select(static field => field.FieldId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var duplicatePolicy in package.AttachmentPolicy.Fields
            .Where(static policy => !string.IsNullOrWhiteSpace(policy.FieldId))
            .GroupBy(static policy => policy.FieldId!, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() > 1))
        {
            AddError(issues, "attachmentPolicyFieldDuplicate", $"Attachment policy references field '{duplicatePolicy.Key}' more than once.", duplicatePolicy.Key);
        }

        foreach (var fieldPolicy in package.AttachmentPolicy.Fields)
        {
            if (string.IsNullOrWhiteSpace(fieldPolicy.FieldId) || !attachmentFieldIds.Contains(fieldPolicy.FieldId))
            {
                AddError(issues, "attachmentPolicyFieldNotFound", $"Attachment policy references unknown attachment field '{fieldPolicy.FieldId}'.", fieldPolicy.FieldId);
            }

            if (fieldPolicy.MaxCount is int fieldMaxCount &&
                (fieldMaxCount <= 0 || fieldMaxCount > maxCount))
            {
                AddError(issues, "attachmentFieldCountLimitInvalid", $"Attachment field max count must be between 1 and {maxCount}.", fieldPolicy.FieldId);
            }

            foreach (var contentType in fieldPolicy.AllowedContentTypes)
            {
                if (!MimeIsAllowedByServer(contentType))
                {
                    AddError(issues, "attachmentContentTypeNotAllowed", $"Attachment content type '{contentType}' is not allowed by server limits.", fieldPolicy.FieldId);
                }

                if (package.AttachmentPolicy.AllowedContentTypes.Length > 0 &&
                    !ContentTypeAllowed(package.AttachmentPolicy.AllowedContentTypes, contentType))
                {
                    AddError(issues, "attachmentFieldContentTypeNotAllowed", $"Attachment field content type '{contentType}' is not allowed by the package attachment policy.", fieldPolicy.FieldId);
                }
            }
        }
    }

    private static void ValidatePrivacyPolicy(FormPackageDocument package, List<FormValidationIssue> issues)
    {
        var fieldIds = package.Fields
            .Where(static field => !string.IsNullOrWhiteSpace(field.FieldId))
            .Select(static field => field.FieldId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var fieldId in package.PrivacyPolicy.PrivateFieldIds)
        {
            if (!fieldIds.Contains(fieldId))
            {
                AddError(issues, "privacyFieldNotFound", $"Privacy policy references unknown field '{fieldId}'.", fieldId);
            }
        }

        foreach (var transform in package.PrivacyPolicy.RequiredTransformations)
        {
            if (!_allowedPrivacyTransforms.Contains(transform))
            {
                AddError(issues, "privacyTransformUnsupported", $"Privacy transformation '{transform}' is not supported by this server.", path: "privacyPolicy.requiredTransformations");
            }
        }

        if (package.PrivacyPolicy.RetentionDays is <= 0)
        {
            AddError(issues, "privacyRetentionInvalid", "privacyPolicy.retentionDays must be greater than zero.", path: "privacyPolicy.retentionDays");
        }
    }

    private static void ValidateOfflinePolicy(FormPackageDocument package, List<FormValidationIssue> issues)
    {
        if (!package.OfflinePolicy.Enabled)
        {
            return;
        }

        foreach (var transport in package.OfflinePolicy.PreferredTransports)
        {
            if (!string.Equals(transport, "feature-server-replica", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(transport, "fieldcollection", StringComparison.OrdinalIgnoreCase))
            {
                AddError(issues, "offlineTransportUnsupported", $"Offline transport '{transport}' is not supported.", path: "offlinePolicy.preferredTransports");
            }
        }

        if (!package.OfflinePolicy.ReplicaTransportEnabled &&
            !package.OfflinePolicy.FieldCollectionTransportEnabled)
        {
            AddError(issues, "offlineTransportRequired", "At least one offline transport must be enabled.", path: "offlinePolicy");
        }

        if (!string.IsNullOrWhiteSpace(package.OfflinePolicy.ConflictReviewMode) &&
            !string.Equals(package.OfflinePolicy.ConflictReviewMode, "defer", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(package.OfflinePolicy.ConflictReviewMode, "lastWriteWins", StringComparison.OrdinalIgnoreCase))
        {
            AddWarning(issues, "conflictReviewDeferred", "Full conflict review is out of scope for this API version.", path: "offlinePolicy.conflictReviewMode");
        }
    }

    private static void ValidateSubmissionPolicy(
        FormPackageDocument package,
        FormSubmissionRequest request,
        MetadataV2Resource resource,
        List<FormValidationIssue> issues)
    {
        if (!IsKnownOperation(request.Operation))
        {
            AddError(issues, "operationUnsupported", $"Submission operation '{request.Operation}' is not supported.", path: "operation");
            return;
        }

        if (!package.SubmitPolicy.AllowedOperations.Contains(request.Operation, StringComparer.OrdinalIgnoreCase))
        {
            AddError(issues, "operationNotAllowed", $"Operation '{request.Operation}' is not allowed by the published package.", path: "operation");
        }

        if ((OperationEquals(request.Operation, FormSubmissionOperations.Update) ||
             OperationEquals(request.Operation, FormSubmissionOperations.Delete)) &&
            request.TargetFeatureId is null)
        {
            AddError(issues, "targetFeatureIdRequired", "targetFeatureId is required for update and delete submissions.", path: "targetFeatureId");
        }

        if (request.SubmittedAt is DateTimeOffset submittedAt &&
            package.SubmitPolicy.MaxOfflineAgeSeconds is int maxAgeSeconds &&
            DateTimeOffset.UtcNow - submittedAt.ToUniversalTime() > TimeSpan.FromSeconds(maxAgeSeconds))
        {
            AddError(issues, "offlineSubmissionExpired", "Submission exceeds submitPolicy.maxOfflineAgeSeconds.", path: "submittedAt");
        }

        if (package.SubmitPolicy.RequiresGeometry &&
            ResourceGeometryType(resource) != MetadataV2GeometryType.None &&
            !OperationEquals(request.Operation, FormSubmissionOperations.Delete) &&
            request.Geometry is null)
        {
            AddError(issues, "geometryRequired", "geometry is required for this package and layer.", path: "geometry");
        }
    }

    private static void ValidateSubmittedValues(
        FormPackageDocument package,
        FormSubmissionRequest request,
        MetadataV2Resource resource,
        List<FormValidationIssue> issues)
    {
        var fields = package.Fields
            .Where(static field => !string.IsNullOrWhiteSpace(field.FieldId))
            .ToDictionary(static field => field.FieldId!, StringComparer.OrdinalIgnoreCase);
        var targetFields = resource.SchemaFields.ToDictionary(static field => field.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var submittedField in request.Values.Keys)
        {
            if (!fields.ContainsKey(submittedField))
            {
                AddError(issues, "fieldNotInPackage", $"Submitted field '{submittedField}' is not defined by the package.", submittedField);
            }
        }

        if (OperationEquals(request.Operation, FormSubmissionOperations.Delete))
        {
            return;
        }

        foreach (var field in fields.Values)
        {
            if (IsAttachmentField(field))
            {
                continue;
            }

            if (field.ReadOnly && request.Values.ContainsKey(field.FieldId!))
            {
                AddError(issues, "readOnlyFieldSubmitted", $"Field '{field.FieldId}' is read-only.", field.FieldId);
                continue;
            }

            var hasValue = request.Values.TryGetValue(field.FieldId!, out var value) && value.ValueKind != JsonValueKind.Null;
            if (field.Required && !hasValue)
            {
                AddError(issues, "requiredFieldMissing", $"Required field '{field.FieldId}' is missing.", field.FieldId);
                continue;
            }

            if (!hasValue)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(field.TargetField) &&
                targetFields.TryGetValue(field.TargetField, out var layerField))
            {
                ValidateSubmittedValueType(field, layerField, value, issues);
                ValidateSubmittedDomain(field, value, issues);
            }
        }
    }

    private static void ValidateSubmittedValueType(
        FormFieldDefinition field,
        MetadataV2Field layerField,
        JsonElement value,
        List<FormValidationIssue> issues)
    {
        if (!IsCompatibleJsonKind(value, layerField.Type))
        {
            AddError(issues, "fieldValueTypeMismatch", $"Submitted value for '{field.FieldId}' is not compatible with target field '{layerField.Name}'.", field.FieldId);
        }

        if (layerField.Type == MetadataV2FieldType.String &&
            layerField.Length is int maxLength &&
            value.ValueKind == JsonValueKind.String &&
            value.GetString() is string text &&
            text.Length > maxLength)
        {
            AddError(issues, "fieldValueTooLong", $"Submitted value for '{field.FieldId}' exceeds target length {maxLength}.", field.FieldId);
        }
    }

    private static void ValidateSubmittedDomain(
        FormFieldDefinition field,
        JsonElement value,
        List<FormValidationIssue> issues)
    {
        if (field.Domain is null)
        {
            return;
        }

        if (string.Equals(field.Domain.Type, "codedValue", StringComparison.OrdinalIgnoreCase))
        {
            var submitted = NormalizeJson(value);
            var allowed = field.Domain.Choices
                .Select(static choice => NormalizeJson(choice.Code))
                .ToHashSet(StringComparer.Ordinal);
            if (!allowed.Contains(submitted))
            {
                AddError(issues, "fieldValueOutsideDomain", $"Submitted value for '{field.FieldId}' is outside the form domain.", field.FieldId);
            }
        }
    }

    private static void ValidateSubmittedGeometry(
        FormPackageDocument package,
        FormSubmissionRequest request,
        MetadataV2Resource resource,
        List<FormValidationIssue> issues)
    {
        _ = package;
        if (request.Geometry is null || OperationEquals(request.Operation, FormSubmissionOperations.Delete))
        {
            return;
        }

        var geometry = request.Geometry.Value;
        if (geometry.ValueKind != JsonValueKind.Object)
        {
            AddError(issues, "geometryInvalid", "geometry must be a JSON object.", path: "geometry");
            return;
        }

        if (!geometry.TryGetProperty("x", out var x) ||
            !geometry.TryGetProperty("y", out var y))
        {
            AddError(issues, "geometryPointRequired", "This form API currently accepts point geometry with x and y.", path: "geometry");
            return;
        }

        if (!TryReadFiniteDouble(x, out _) || !TryReadFiniteDouble(y, out _))
        {
            AddError(issues, "geometryCoordinateInvalid", "geometry x and y must be finite numbers or numeric strings.", path: "geometry");
            return;
        }

        var submittedSrid = ResolveGeometrySrid(geometry);
        var layerSrid = resource.Spatial?.SpatialReference?.ResolveSrid();
        if (submittedSrid is int srid &&
            layerSrid is int targetSrid &&
            NormalizeSrid(srid) != NormalizeSrid(targetSrid))
        {
            AddError(issues, "geometrySridMismatch", $"Submitted geometry SRID {srid} does not match target layer SRID {targetSrid}.", path: "geometry.spatialReference");
        }
    }

    private void ValidateSubmittedAttachments(
        FormPackageDocument package,
        FormSubmissionRequest request,
        MetadataV2Resource resource,
        List<FormValidationIssue> issues)
    {
        if (OperationEquals(request.Operation, FormSubmissionOperations.Delete))
        {
            if (request.Attachments.Length > 0)
            {
                AddError(issues, "attachmentsNotAllowedForDelete", "Delete submissions cannot include attachment descriptors.", path: "attachments");
            }

            return;
        }

        var attachmentFields = package.Fields
            .Where(IsAttachmentField)
            .Where(static field => !string.IsNullOrWhiteSpace(field.FieldId))
            .ToArray();
        var attachmentFieldIds = attachmentFields
            .Select(static field => field.FieldId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fieldPolicies = BuildAttachmentFieldPolicies(package.AttachmentPolicy);
        var requiredAttachmentFields = attachmentFields
            .Where(field => field.Required ||
                (fieldPolicies.TryGetValue(field.FieldId!, out var fieldPolicy) && fieldPolicy.Required))
            .Select(static field => field.FieldId!)
            .ToArray();

        if (request.Attachments.Length == 0)
        {
            foreach (var fieldId in requiredAttachmentFields)
            {
                AddError(issues, "requiredAttachmentMissing", $"Required attachment field '{fieldId}' is missing.", fieldId);
            }

            return;
        }

        if (!package.SubmitPolicy.AllowAttachments || !package.AttachmentPolicy.Enabled)
        {
            AddError(issues, "attachmentsNotAllowed", "Attachments are not allowed by the published package.", path: "attachments");
            return;
        }

        if (!ResourceSupportsAttachments(resource))
        {
            AddError(issues, "attachmentsNotSupported", $"Target layer '{resource.Metadata.Name}' does not support attachments.", path: "attachments");
        }

        var maxCount = package.AttachmentPolicy.MaxAttachmentsPerSubmission ?? _attachmentLimits.MaxAttachmentsPerFeature;
        if (request.Attachments.Length > maxCount)
        {
            AddError(issues, "attachmentCountExceeded", $"Submission includes {request.Attachments.Length} attachments; maximum is {maxCount}.", path: "attachments");
        }

        long totalSize = 0;
        foreach (var group in request.Attachments
            .Where(static attachment => !string.IsNullOrWhiteSpace(attachment.FieldId))
            .GroupBy(static attachment => attachment.FieldId!, StringComparer.OrdinalIgnoreCase))
        {
            if (fieldPolicies.TryGetValue(group.Key, out var fieldPolicy) &&
                fieldPolicy.MaxCount is int maxFieldCount &&
                group.Count() > maxFieldCount)
            {
                AddError(issues, "attachmentFieldCountExceeded", $"Attachment field '{group.Key}' includes {group.Count()} attachments; maximum is {maxFieldCount}.", group.Key);
            }
        }

        foreach (var attachment in request.Attachments)
        {
            FormFieldAttachmentPolicy? fieldPolicy = null;
            if (string.IsNullOrWhiteSpace(attachment.FieldId) || !attachmentFieldIds.Contains(attachment.FieldId))
            {
                AddError(issues, "attachmentFieldNotFound", $"Attachment field '{attachment.FieldId}' is not defined as an attachment field.", attachment.FieldId);
            }
            else
            {
                fieldPolicies.TryGetValue(attachment.FieldId, out fieldPolicy);
            }

            if (string.IsNullOrWhiteSpace(attachment.PartName))
            {
                AddError(issues, "attachmentPartNameRequired", "Attachment partName is required.", attachment.FieldId);
            }

            if (attachment.SizeBytes is long size)
            {
                totalSize += size;
                var maxSize = package.AttachmentPolicy.MaxAttachmentBytes ?? _attachmentLimits.MaxAttachmentSize;
                if (size <= 0 || size > maxSize)
                {
                    AddError(issues, "attachmentSizeInvalid", $"Attachment size must be between 1 and {maxSize} bytes.", attachment.FieldId);
                }
            }

            if (!string.IsNullOrWhiteSpace(attachment.ContentType) &&
                !AttachmentContentTypeAllowed(package.AttachmentPolicy, fieldPolicy, attachment.ContentType))
            {
                AddError(issues, "attachmentContentTypeNotAllowed", $"Attachment content type '{attachment.ContentType}' is not allowed.", attachment.FieldId);
            }
        }

        foreach (var fieldId in requiredAttachmentFields)
        {
            if (!request.Attachments.Any(attachment => string.Equals(attachment.FieldId, fieldId, StringComparison.OrdinalIgnoreCase)))
            {
                AddError(issues, "requiredAttachmentMissing", $"Required attachment field '{fieldId}' is missing.", fieldId);
            }
        }

        var maxTotal = package.AttachmentPolicy.MaxTotalBytes ?? _attachmentLimits.MaxTotalAttachmentSize;
        if (totalSize > maxTotal)
        {
            AddError(issues, "attachmentTotalSizeExceeded", $"Submission attachment total exceeds {maxTotal} bytes.", path: "attachments");
        }
    }

    private bool MimeIsAllowedByServer(string contentType)
        => ContentTypeAllowed(SplitAllowedMimeTypes(_attachmentLimits.AllowedMimeTypes), contentType);

    private bool AttachmentContentTypeAllowed(
        FormAttachmentPolicy policy,
        FormFieldAttachmentPolicy? fieldPolicy,
        string contentType)
    {
        if (fieldPolicy?.AllowedContentTypes.Length > 0 &&
            !ContentTypeAllowed(fieldPolicy.AllowedContentTypes, contentType))
        {
            return false;
        }

        if (policy.AllowedContentTypes.Length > 0 &&
            !ContentTypeAllowed(policy.AllowedContentTypes, contentType))
        {
            return false;
        }

        return MimeIsAllowedByServer(contentType);
    }

    private static Dictionary<string, FormFieldAttachmentPolicy> BuildAttachmentFieldPolicies(FormAttachmentPolicy policy)
        => policy.Fields
            .Where(static fieldPolicy => !string.IsNullOrWhiteSpace(fieldPolicy.FieldId))
            .GroupBy(static fieldPolicy => fieldPolicy.FieldId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);

    private static bool ContentTypeAllowed(IEnumerable<string> allowedContentTypes, string contentType)
    {
        var normalized = contentType.Split(';', 2)[0].Trim();
        foreach (var allowed in allowedContentTypes)
        {
            if (string.IsNullOrWhiteSpace(allowed))
            {
                continue;
            }

            var pattern = allowed.Trim();
            if (string.Equals(pattern, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (pattern.EndsWith("/*", StringComparison.Ordinal) &&
                normalized.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string[] SplitAllowedMimeTypes(string allowedMimeTypes)
        => allowedMimeTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsAttachmentField(FormFieldDefinition field)
        => string.Equals(field.Type, "attachment", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(field.Type, "media", StringComparison.OrdinalIgnoreCase);

    private static bool IsKnownOperation(string operation)
        => string.Equals(operation, FormSubmissionOperations.Create, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(operation, FormSubmissionOperations.Update, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(operation, FormSubmissionOperations.Delete, StringComparison.OrdinalIgnoreCase);

    private static bool OperationEquals(string operation, string expected)
        => string.Equals(operation, expected, StringComparison.OrdinalIgnoreCase);

    private static bool TargetSupportsOperation(
        MetadataV2Service service,
        MetadataV2Publication publication,
        MetadataV2Resource resource,
        string operation)
    {
        if (resource.Editing is { CanModify: false })
        {
            return false;
        }

        var capability = ToServiceCapability(operation);
        return ContainsCapability(publication.Capabilities, capability) ||
               ContainsCapability(ReadServiceCapabilities(service), capability);
    }

    private static bool ContainsCapability(IEnumerable<string> capabilities, string capability)
        => capabilities.Any(candidate =>
            string.Equals(candidate, capability, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate, $"FeatureServer.{capability}", StringComparison.OrdinalIgnoreCase));

    private static List<string> ReadServiceCapabilities(MetadataV2Service service)
    {
        if (service.Options.TryGetValue("capabilities", out var element) &&
            element.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>(element.GetArrayLength());
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String &&
                    item.GetString() is { Length: > 0 } value)
                {
                    list.Add(value);
                }
            }

            if (list.Count > 0)
            {
                return list;
            }
        }

        return ["Query"];
    }

    private static bool ResourceSupportsAttachments(MetadataV2Resource resource)
        => resource.Editing?.SupportsAttachments == true ||
           (TryReadBoolAnnotation(resource.Metadata.Annotations, "honua.io/attachments") ??
            TryReadBoolAnnotation(resource.Metadata.Annotations, "supportsAttachments") ??
            false);

    private static bool? TryReadBoolAnnotation(IReadOnlyDictionary<string, string> annotations, string key)
    {
        if (annotations.TryGetValue(key, out var raw) &&
            bool.TryParse(raw, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static MetadataV2GeometryType ResourceGeometryType(MetadataV2Resource resource)
        => resource.Spatial?.GeometryType ?? MetadataV2GeometryType.None;

    private static bool IsGeometryField(MetadataV2Field field)
        => field.Type is MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography;

    private static string ToServiceCapability(string operation)
        => string.Equals(operation, FormSubmissionOperations.Create, StringComparison.OrdinalIgnoreCase)
            ? "Create"
            : string.Equals(operation, FormSubmissionOperations.Update, StringComparison.OrdinalIgnoreCase)
                ? "Update"
                : "Delete";

    private static bool IsCompatibleFieldType(string? formType, MetadataV2FieldType layerType)
    {
        if (string.IsNullOrWhiteSpace(formType))
        {
            return false;
        }

        return formType.ToLowerInvariant() switch
        {
            "text" or "string" or "email" or "barcode" or "choice" => layerType == MetadataV2FieldType.String,
            "integer" or "int" => layerType is MetadataV2FieldType.Integer or MetadataV2FieldType.BigInteger,
            "number" or "decimal" or "double" => layerType is MetadataV2FieldType.Double or MetadataV2FieldType.Float,
            "boolean" or "bool" => layerType == MetadataV2FieldType.Boolean,
            "date" => layerType is MetadataV2FieldType.Date or MetadataV2FieldType.DateTime,
            "datetime" => layerType == MetadataV2FieldType.DateTime,
            "uuid" or "guid" => layerType == MetadataV2FieldType.Uuid,
            "json" => layerType == MetadataV2FieldType.Json,
            _ => false
        };
    }

    private static bool IsCompatibleJsonKind(JsonElement value, MetadataV2FieldType layerType)
        => layerType switch
        {
            MetadataV2FieldType.String or MetadataV2FieldType.Uuid or MetadataV2FieldType.Date or MetadataV2FieldType.DateTime or MetadataV2FieldType.Time => value.ValueKind == JsonValueKind.String,
            MetadataV2FieldType.Integer => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _),
            MetadataV2FieldType.BigInteger => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            MetadataV2FieldType.Double => TryReadFiniteJsonNumber(value, out _),
            MetadataV2FieldType.Float => value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out var singleValue) && float.IsFinite(singleValue),
            MetadataV2FieldType.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            MetadataV2FieldType.Json => true,
            _ => true
        };

    private static bool TryReadFiniteJsonNumber(JsonElement value, out double result)
    {
        result = 0;
        return value.ValueKind == JsonValueKind.Number &&
               value.TryGetDouble(out result) &&
               double.IsFinite(result);
    }

    private static bool TryGetNumericParameter(JsonElement? parameters, string name, out int value)
    {
        value = 0;
        if (parameters is not JsonElement element || element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!element.TryGetProperty(name, out var property))
        {
            return false;
        }

        return property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value);
    }

    private static bool TryReadFiniteDouble(JsonElement value, out double result)
    {
        result = 0;
        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.TryGetDouble(out result) && double.IsFinite(result);
        }

        return value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result) &&
            double.IsFinite(result);
    }

    private static int? ResolveGeometrySrid(JsonElement geometry)
    {
        if (!geometry.TryGetProperty("spatialReference", out var spatialReference) ||
            spatialReference.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (spatialReference.TryGetProperty("latestWkid", out var latestWkid) && latestWkid.TryGetInt32(out var srid))
        {
            return srid;
        }

        if (spatialReference.TryGetProperty("wkid", out var wkid) && wkid.TryGetInt32(out srid))
        {
            return srid;
        }

        return null;
    }

    private static int NormalizeSrid(int srid)
        => srid is 102100 or 102113 or 900913 or 3785 ? 3857 : srid;

    private static string JsonElementToString(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Undefined or JsonValueKind.Null => string.Empty,
            _ => element.GetRawText()
        };

    private static string NormalizeJson(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Undefined or JsonValueKind.Null => string.Empty,
            _ => element.GetRawText()
        };

    private static FormPackageValidationResult CreateResult(List<FormValidationIssue> issues)
        => new()
        {
            IsValid = !issues.Any(static issue => string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase)),
            Issues = issues.ToArray()
        };

    private static void AddError(
        List<FormValidationIssue> issues,
        string code,
        string message,
        string? fieldId = null,
        string? path = null)
        => issues.Add(new FormValidationIssue
        {
            Code = code,
            Severity = "error",
            FieldId = fieldId,
            Path = path,
            Message = message
        });

    private static void AddWarning(
        List<FormValidationIssue> issues,
        string code,
        string message,
        string? fieldId = null,
        string? path = null)
        => issues.Add(new FormValidationIssue
        {
            Code = code,
            Severity = "warning",
            FieldId = fieldId,
            Path = path,
            Message = message
        });
}
