// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Forms.Packages;

public sealed partial class FormPackageValidator
{
    private static void ValidateFields(FormPackageDocument package, MetadataV2Object resource, List<FormValidationIssue> issues)
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

            ValidateFieldIdentity(field, sectionIds, issues);

            if (UsesVirtualFieldBinding(package, field))
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

            ValidateFieldBinding(field, new(layerField, package.ExpressionLanguage), issues);
            ValidateVisibilityRule(field, package.Fields, issues);
        }

        ValidateVisibilityCycles(package.Fields, issues);
    }

    private static void ValidateFieldIdentity(
        FormFieldDefinition field, HashSet<string> sectionIds, List<FormValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(field.Label))
        {
            AddError(issues, "fieldLabelRequired", $"Field '{field.FieldId}' requires a label.", field.FieldId);
        }

        if (!string.IsNullOrWhiteSpace(field.SectionId) && !sectionIds.Contains(field.SectionId))
        {
            AddError(issues, "sectionNotFound", $"Field '{field.FieldId}' references unknown section '{field.SectionId}'.", field.FieldId);
        }
    }

    private static bool UsesVirtualFieldBinding(FormPackageDocument package, FormFieldDefinition field)
        => IsAttachmentField(field) || FormXlsFormExpressions.IsEnabled(package) &&
            (IsXlsLocation(field) || package.Sections.Any(section => section.Repeatable && section.SectionId == field.SectionId));

    private sealed record FieldBinding(MetadataV2Field LayerField, string? ExpressionLanguage);

    private static void ValidateFieldBinding(
        FormFieldDefinition field, FieldBinding binding, List<FormValidationIssue> issues)
    {
        var layerField = binding.LayerField;
        var isXlsForm = binding.ExpressionLanguage == FormXlsFormExpressions.Language;
        if (IsGeometryField(layerField) || layerField.Hidden || !layerField.Editable)
        {
            AddError(issues, "targetFieldNotWritable", $"Target field '{layerField.Name}' is not writable.", field.FieldId);
        }

        if (!(isXlsForm && IsXlsMultipleChoice(field)
            ? layerField.Type == MetadataV2FieldType.String
            : IsCompatibleFieldType(field.Type, layerField.Type)))
        {
            AddError(
                issues,
                "targetFieldTypeMismatch",
                $"Field '{field.FieldId}' type '{field.Type}' is not compatible with target field '{layerField.Name}' type '{layerField.Type}'.",
                field.FieldId);
        }

        if (!layerField.Nullable &&
            ((!field.Required && field.DefaultValue is null && field.CalculatedExpression is null) ||
             (isXlsForm && field.RelevanceExpression is not null)))
        {
            AddError(
                issues,
                "requiredStateMismatch",
                $"Target field '{layerField.Name}' is non-nullable; form field '{field.FieldId}' must be required or declare a default value.",
                field.FieldId);
        }

        if (isXlsForm && IsXlsMultipleChoice(field) &&
            layerField.Domain?.CodedValues is { Count: > 0 })
        {
            AddError(issues, "xlsFormChoiceTargetInvalid", "Multiple choices need a text attribute without a coded domain.", field.FieldId);
        }
        ValidateFieldDomain(field, layerField, issues);
        ValidateFieldValidationRules(field, layerField, issues);
    }
}
