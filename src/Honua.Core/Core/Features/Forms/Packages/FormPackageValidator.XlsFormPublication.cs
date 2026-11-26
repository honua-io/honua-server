// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Forms.Packages;

public sealed partial class FormPackageValidator
{
    internal static void ValidateXlsExpressions(FormPackageDocument package, List<FormValidationIssue> issues)
    {
        var parentFields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (scope, fields) in GetXlsScopes(package))
        {
            var visibleFields = scope.Length == 0
                ? parentFields
                : new HashSet<string>(parentFields, StringComparer.Ordinal);
            foreach (var field in fields.Where(static field => !string.IsNullOrEmpty(field.FieldId)))
            {
                ValidateXlsFieldExpressions(field, visibleFields, issues);
            }
        }
    }

    private static void ValidateXlsFieldExpressions(
        FormFieldDefinition field, HashSet<string> visibleFields, List<FormValidationIssue> issues)
    {
        // Earlier answers only. Repeat rows share parent answers but cannot see each other's answers.
        var virtualField = IsAttachmentField(field) || IsXlsLocation(field);
        if (field.RelevanceExpression is { } relevance && FormXlsFormExpressions.Compile(relevance, visibleFields) is null)
        {
            AddUnsupported("relevanceExpression");
        }
        if (field.CalculatedExpression is { } calculation &&
            (virtualField || FormXlsFormExpressions.Compile(calculation, visibleFields) is null))
        {
            AddUnsupported("calculatedExpression");
        }
        if (!virtualField)
        {
            visibleFields.Add(field.FieldId!);
        }
        if (field.ConstraintExpression is { } constraint &&
            (virtualField || FormXlsFormExpressions.CompileConstraint(constraint, visibleFields) is null))
        {
            AddUnsupported("constraintExpression");
        }

        void AddUnsupported(string path) => AddError(issues, "xlsFormExpressionUnsupported",
            "Expression uses unsupported syntax or references an answer outside its available scope.", field.FieldId, path);
    }

    private static void ValidateXlsBindings(FormPackageDocument package, MetadataV2Object resource, List<FormValidationIssue> issues)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var locations = 0;
        foreach (var field in GetXlsScopes(package)[string.Empty])
        {
            if (IsXlsLocation(field))
            {
                locations++;
                if (locations > 1 || ResourceGeometryType(resource) != MetadataV2GeometryType.Point ||
                    resource.Spatial?.SpatialReference?.ResolveSrid() != 4326)
                {
                    AddError(issues, "xlsFormGeopointUnsupported", "A geopoint needs a point layer in WGS 84 and only one geopoint is supported.", field.FieldId);
                }
            }
            if (!string.IsNullOrEmpty(field.TargetField) && !targets.Add(field.TargetField))
            {
                AddError(issues, "xlsFormTargetDuplicate", "XLSForm fields must use distinct target attributes.", field.FieldId);
            }
        }

        var repeatSections = package.Sections.Where(static section => section.Repeatable && !string.IsNullOrEmpty(section.SectionId));
        foreach (var section in repeatSections)
        {
            ValidateXlsRepeatBinding(section, new(package, resource, targets), issues);
        }
    }

    private sealed record XlsBindings(FormPackageDocument Package, MetadataV2Object Resource, HashSet<string> Targets);

    private static void ValidateXlsRepeatBinding(
        FormSectionDefinition section, XlsBindings bindings, List<FormValidationIssue> issues)
    {
        var target = bindings.Resource.SchemaFields.FirstOrDefault(field =>
            string.Equals(field.Name, section.SectionId, StringComparison.OrdinalIgnoreCase));
        if (target is null || target.Type != MetadataV2FieldType.String || target.Hidden || !target.Editable ||
            target.Domain?.CodedValues is { Count: > 0 } || !bindings.Targets.Add(section.SectionId!))
        {
            AddError(issues, "xlsFormRepeatTargetInvalid", "The repeat section needs a distinct writable text attribute with the same name and no coded domain.", section.SectionId);
        }
        if (bindings.Package.Fields.Any(field => string.Equals(field.FieldId, section.SectionId, StringComparison.OrdinalIgnoreCase)) ||
            !bindings.Package.Fields.Any(field => field.SectionId == section.SectionId))
        {
            AddError(issues, "xlsFormRepeatSectionInvalid", "A repeat section must have its own submission key and contain questions.", section.SectionId);
        }
        if (section.MinInstances > MaxRepeatRows || section.MaxInstances > MaxRepeatRows)
        {
            AddError(issues, "xlsFormRepeatLimit", $"Repeat sections support up to {MaxRepeatRows} rows.", section.SectionId);
        }
        foreach (var field in bindings.Package.Fields.Where(field => field.SectionId == section.SectionId))
        {
            if (IsAttachmentField(field) || IsXlsLocation(field) || !string.IsNullOrEmpty(field.TargetField))
            {
                AddError(issues, "xlsFormRepeatFieldUnsupported",
                    "Repeat questions must store their scalar or multiple-choice answers inside the repeat section.", field.FieldId);
            }
        }
    }
}
