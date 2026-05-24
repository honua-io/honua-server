// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
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

    private readonly ILayerCatalog _catalog;
    private readonly AttachmentLimits _attachmentLimits;

    /// <summary>
    /// Initializes a new validator.
    /// </summary>
    public FormPackageValidator(ILayerCatalog catalog, IOptions<LimitsOptions> limitsOptions)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
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

        var (service, layer) = await ResolveTargetAsync(package, issues, cancellationToken).ConfigureAwait(false);
        if (service is not null && layer is not null)
        {
            ValidateSubmitPolicy(package, service, layer, issues);
            ValidateFields(package, layer, issues);
            ValidateSections(package, issues);
            ValidateAttachmentPolicy(package, layer, issues);
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
        var (service, layer) = await ResolveTargetAsync(package, issues, cancellationToken).ConfigureAwait(false);
        if (service is null || layer is null)
        {
            return CreateResult(issues);
        }

        ValidateSubmissionPolicy(package, request, layer, issues);
        ValidateSubmittedValues(package, request, layer, issues);
        ValidateSubmittedGeometry(package, request, layer, issues);
        ValidateSubmittedAttachments(package, request, layer, issues);

        return CreateResult(issues);
    }

    /// <summary>
    /// Resolves the target service and layer for a package.
    /// </summary>
    public async Task<(ServiceDefinition? Service, LayerDefinition? Layer)> ResolveTargetAsync(
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
            return (null, null);
        }

        var service = await _catalog.GetServiceAsync(target.ServiceId, cancellationToken).ConfigureAwait(false);
        if (service is null)
        {
            AddError(issues, "serviceNotFound", $"Target service '{target.ServiceId}' was not found.", path: "target.serviceId");
            return (null, null);
        }

        var layer = service.GetLayer(target.LayerId);
        if (layer is null)
        {
            AddError(issues, "layerNotFound", $"Target layer '{target.LayerId}' was not found in service '{target.ServiceId}'.", path: "target.layerId");
            return (service, null);
        }

        return (service, layer);
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
        ServiceDefinition service,
        LayerDefinition layer,
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

            if (!ServiceSupportsOperation(service, operation))
            {
                AddError(
                    issues,
                    "targetOperationUnsupported",
                    $"Target service '{service.Name}' does not advertise '{ToServiceCapability(operation)}' capability.",
                    path: "submitPolicy.allowedOperations");
            }
        }

        if (package.SubmitPolicy.RequiresGeometry && layer.GeometryType == GeometryType.None)
        {
            AddWarning(issues, "geometryNotAvailable", "submitPolicy.requiresGeometry is true, but the target layer has no geometry.", path: "submitPolicy.requiresGeometry");
        }
    }

    private static void ValidateFields(FormPackageDocument package, LayerDefinition layer, List<FormValidationIssue> issues)
    {
        var sectionIds = package.Sections
            .Where(static section => !string.IsNullOrWhiteSpace(section.SectionId))
            .Select(static section => section.SectionId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var layerFields = layer.Fields.ToDictionary(static field => field.Name, StringComparer.OrdinalIgnoreCase);

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
                AddError(issues, "targetFieldNotFound", $"Target field '{field.TargetField}' was not found on layer '{layer.Name}'.", field.FieldId);
                continue;
            }

            if (layerField.IsGeometry || layerField.IsHidden)
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
        FieldDefinition layerField,
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

            var duplicateCodes = field.Domain.Choices
                .GroupBy(static choice => NormalizeJson(choice.Code), StringComparer.Ordinal)
                .Where(static group => group.Count() > 1);
            foreach (var duplicate in duplicateCodes)
            {
                AddError(issues, "domainChoiceDuplicate", $"Domain choice code '{duplicate.Key}' is duplicated.", field.FieldId);
            }

            if (layerField.Domain?.CodedValues is { Length: > 0 } layerChoices)
            {
                var allowedCodes = layerChoices
                    .Select(static value => Convert.ToString(value.Code, CultureInfo.InvariantCulture) ?? string.Empty)
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
        FieldDefinition layerField,
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

    private void ValidateAttachmentPolicy(FormPackageDocument package, LayerDefinition layer, List<FormValidationIssue> issues)
    {
        var attachmentFields = package.Fields.Where(IsAttachmentField).ToArray();
        if (!package.AttachmentPolicy.Enabled && attachmentFields.Length == 0)
        {
            return;
        }

        if (!layer.SupportsAttachments)
        {
            AddError(issues, "attachmentsNotSupported", $"Target layer '{layer.Name}' does not support attachments.", path: "attachmentPolicy");
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
        foreach (var fieldPolicy in package.AttachmentPolicy.Fields)
        {
            if (string.IsNullOrWhiteSpace(fieldPolicy.FieldId) || !attachmentFieldIds.Contains(fieldPolicy.FieldId))
            {
                AddError(issues, "attachmentPolicyFieldNotFound", $"Attachment policy references unknown attachment field '{fieldPolicy.FieldId}'.", fieldPolicy.FieldId);
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
        LayerDefinition layer,
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

        if (request.Operation is FormSubmissionOperations.Update or FormSubmissionOperations.Delete && request.TargetFeatureId is null)
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
            layer.GeometryType != GeometryType.None &&
            request.Operation != FormSubmissionOperations.Delete &&
            request.Geometry is null)
        {
            AddError(issues, "geometryRequired", "geometry is required for this package and layer.", path: "geometry");
        }
    }

    private static void ValidateSubmittedValues(
        FormPackageDocument package,
        FormSubmissionRequest request,
        LayerDefinition layer,
        List<FormValidationIssue> issues)
    {
        var fields = package.Fields
            .Where(static field => !string.IsNullOrWhiteSpace(field.FieldId))
            .ToDictionary(static field => field.FieldId!, StringComparer.OrdinalIgnoreCase);
        var targetFields = layer.Fields.ToDictionary(static field => field.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var submittedField in request.Values.Keys)
        {
            if (!fields.ContainsKey(submittedField))
            {
                AddError(issues, "fieldNotInPackage", $"Submitted field '{submittedField}' is not defined by the package.", submittedField);
            }
        }

        if (request.Operation == FormSubmissionOperations.Delete)
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
        FieldDefinition layerField,
        JsonElement value,
        List<FormValidationIssue> issues)
    {
        if (!IsCompatibleJsonKind(value, layerField.Type))
        {
            AddError(issues, "fieldValueTypeMismatch", $"Submitted value for '{field.FieldId}' is not compatible with target field '{layerField.Name}'.", field.FieldId);
        }

        if (layerField.Type == FieldType.String &&
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
        LayerDefinition layer,
        List<FormValidationIssue> issues)
    {
        _ = package;
        if (request.Geometry is null || request.Operation == FormSubmissionOperations.Delete)
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
            !geometry.TryGetProperty("y", out var y) ||
            x.ValueKind is not (JsonValueKind.Number or JsonValueKind.String) ||
            y.ValueKind is not (JsonValueKind.Number or JsonValueKind.String))
        {
            AddError(issues, "geometryPointRequired", "This form API currently accepts point geometry with x and y.", path: "geometry");
            return;
        }

        var submittedSrid = ResolveGeometrySrid(geometry);
        if (submittedSrid is int srid && srid != layer.SpatialReference.Wkid)
        {
            AddError(issues, "geometrySridMismatch", $"Submitted geometry SRID {srid} does not match target layer SRID {layer.SpatialReference.Wkid}.", path: "geometry.spatialReference");
        }
    }

    private void ValidateSubmittedAttachments(
        FormPackageDocument package,
        FormSubmissionRequest request,
        LayerDefinition layer,
        List<FormValidationIssue> issues)
    {
        var requiredAttachmentFields = package.Fields
            .Where(static field => IsAttachmentField(field) && field.Required && !string.IsNullOrWhiteSpace(field.FieldId))
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

        if (!layer.SupportsAttachments)
        {
            AddError(issues, "attachmentsNotSupported", $"Target layer '{layer.Name}' does not support attachments.", path: "attachments");
        }

        var maxCount = package.AttachmentPolicy.MaxAttachmentsPerSubmission ?? _attachmentLimits.MaxAttachmentsPerFeature;
        if (request.Attachments.Length > maxCount)
        {
            AddError(issues, "attachmentCountExceeded", $"Submission includes {request.Attachments.Length} attachments; maximum is {maxCount}.", path: "attachments");
        }

        long totalSize = 0;
        var attachmentFieldIds = package.Fields
            .Where(IsAttachmentField)
            .Where(static field => !string.IsNullOrWhiteSpace(field.FieldId))
            .Select(static field => field.FieldId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var attachment in request.Attachments)
        {
            if (string.IsNullOrWhiteSpace(attachment.FieldId) || !attachmentFieldIds.Contains(attachment.FieldId))
            {
                AddError(issues, "attachmentFieldNotFound", $"Attachment field '{attachment.FieldId}' is not defined as an attachment field.", attachment.FieldId);
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
                !ContentTypeAllowed(package.AttachmentPolicy.AllowedContentTypes, attachment.ContentType) &&
                !MimeIsAllowedByServer(attachment.ContentType))
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

    private static bool ServiceSupportsOperation(ServiceDefinition service, string operation)
        => service.Capabilities.Contains(ToServiceCapability(operation), StringComparer.OrdinalIgnoreCase);

    private static string ToServiceCapability(string operation)
        => string.Equals(operation, FormSubmissionOperations.Create, StringComparison.OrdinalIgnoreCase)
            ? "Create"
            : string.Equals(operation, FormSubmissionOperations.Update, StringComparison.OrdinalIgnoreCase)
                ? "Update"
                : "Delete";

    private static bool IsCompatibleFieldType(string? formType, FieldType layerType)
    {
        if (string.IsNullOrWhiteSpace(formType))
        {
            return false;
        }

        return formType.ToLowerInvariant() switch
        {
            "text" or "string" or "email" or "barcode" or "choice" => layerType == FieldType.String,
            "integer" or "int" => layerType is FieldType.Integer or FieldType.BigInteger,
            "number" or "decimal" or "double" => layerType is FieldType.Double or FieldType.Float,
            "boolean" or "bool" => layerType == FieldType.Boolean,
            "date" => layerType is FieldType.Date or FieldType.DateTime,
            "datetime" => layerType == FieldType.DateTime,
            "uuid" or "guid" => layerType == FieldType.Uuid,
            "json" => layerType == FieldType.Json,
            _ => false
        };
    }

    private static bool IsCompatibleJsonKind(JsonElement value, FieldType layerType)
        => layerType switch
        {
            FieldType.String or FieldType.Uuid or FieldType.Date or FieldType.DateTime or FieldType.Time => value.ValueKind == JsonValueKind.String,
            FieldType.Integer or FieldType.BigInteger or FieldType.Double or FieldType.Float => value.ValueKind == JsonValueKind.Number,
            FieldType.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            FieldType.Json => true,
            _ => true
        };

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

    private static int? ResolveGeometrySrid(JsonElement geometry)
    {
        if (!geometry.TryGetProperty("spatialReference", out var spatialReference) ||
            spatialReference.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (spatialReference.TryGetProperty("wkid", out var wkid) && wkid.TryGetInt32(out var srid))
        {
            return srid;
        }

        if (spatialReference.TryGetProperty("latestWkid", out var latestWkid) && latestWkid.TryGetInt32(out srid))
        {
            return srid;
        }

        return null;
    }

    private static string JsonElementToString(JsonElement element)
        => element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : element.GetRawText();

    private static string NormalizeJson(JsonElement element)
        => element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : element.GetRawText();

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
