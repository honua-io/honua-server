// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Forms.Packages;

public sealed partial class FormPackageValidator
{
    private const int MaxRepeatRows = 100;

    private static bool IsXlsLocation(FormFieldDefinition field) => field.Type == "location";
    private static bool IsXlsMultipleChoice(FormFieldDefinition field) => field.Type == "multiChoice";

    private static Dictionary<string, FormFieldDefinition[]> GetXlsScopes(FormPackageDocument package)
    {
        var scopes = new Dictionary<string, FormFieldDefinition[]>(StringComparer.Ordinal)
        {
            [string.Empty] = package.Fields.Where(field => !package.Sections.Any(section =>
                section.Repeatable && section.SectionId == field.SectionId)).ToArray()
        };
        foreach (var section in package.Sections.Where(section => section.Repeatable && !string.IsNullOrEmpty(section.SectionId)))
        {
            scopes.TryAdd(section.SectionId!, package.Fields.Where(field => field.SectionId == section.SectionId).ToArray());
        }
        return scopes;
    }

    internal static void ValidateXlsExpressions(FormPackageDocument package, List<FormValidationIssue> issues)
    {
        var scopes = GetXlsScopes(package);
        var parentFields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (scope, fields) in scopes)
        {
            var visibleFields = new HashSet<string>(parentFields, StringComparer.Ordinal);
            foreach (var field in fields)
            {
                var fieldId = field.FieldId;
                if (string.IsNullOrEmpty(fieldId))
                {
                    continue;
                }

                // Only earlier answers are available. This prevents cycles, stale values from
                // hidden questions, and dependencies on an answer from a different repeat row.
                Check(field.RelevanceExpression, "relevanceExpression", allowSelf: false);
                Check(field.CalculatedExpression, "calculatedExpression", allowSelf: false);

                if (!IsAttachmentField(field) && !IsXlsLocation(field))
                {
                    visibleFields.Add(fieldId);
                    if (scope.Length == 0)
                    {
                        parentFields.Add(fieldId);
                    }
                }
                Check(field.ConstraintExpression, "constraintExpression", allowSelf: true);

                void Check(string? expression, string path, bool allowSelf)
                {
                    if (expression is not null &&
                        ((IsAttachmentField(field) || IsXlsLocation(field)) && path != "relevanceExpression" ||
                         !FormXlsFormExpressions.TryCompile(expression, visibleFields, allowSelf, out _)))
                    {
                        AddError(issues, "xlsFormExpressionUnsupported",
                            "Expression uses unsupported syntax or references an answer outside its available scope.", fieldId, path);
                    }
                }
            }
        }
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

        foreach (var section in package.Sections.Where(static section => section.Repeatable && !string.IsNullOrEmpty(section.SectionId)))
        {
            var target = resource.SchemaFields.FirstOrDefault(field =>
                string.Equals(field.Name, section.SectionId, StringComparison.OrdinalIgnoreCase));
            if (target is null || target.Type != MetadataV2FieldType.String || target.Hidden || !target.Editable ||
                target.Domain?.CodedValues is { Count: > 0 } || !targets.Add(section.SectionId!))
            {
                AddError(issues, "xlsFormRepeatTargetInvalid", "The repeat section needs a distinct writable text attribute with the same name and no coded domain.", section.SectionId);
            }
            if (package.Fields.Any(field => string.Equals(field.FieldId, section.SectionId, StringComparison.OrdinalIgnoreCase)) ||
                !package.Fields.Any(field => field.SectionId == section.SectionId))
            {
                AddError(issues, "xlsFormRepeatSectionInvalid", "A repeat section must have its own submission key and contain questions.", section.SectionId);
            }
            if (section.MinInstances > MaxRepeatRows || section.MaxInstances > MaxRepeatRows)
            {
                AddError(issues, "xlsFormRepeatLimit", $"Repeat sections support up to {MaxRepeatRows} rows.", section.SectionId);
            }
            foreach (var field in package.Fields.Where(field => field.SectionId == section.SectionId))
            {
                if (IsAttachmentField(field) || IsXlsLocation(field) || !string.IsNullOrEmpty(field.TargetField))
                {
                    AddError(issues, "xlsFormRepeatFieldUnsupported",
                        "Repeat questions must store their scalar or multiple-choice answers inside the repeat section.", field.FieldId);
                }
            }
        }
    }

    private static void NormalizeXlsSubmission(
        FormPackageDocument package, FormSubmissionRequest request, MetadataV2Object resource,
        List<FormValidationIssue> issues)
    {
        var scopes = GetXlsScopes(package);
        var mainFields = scopes[string.Empty];
        var knownKeys = mainFields.Select(static field => field.FieldId)
            .Concat(scopes.Keys.Where(static key => key.Length > 0)).ToHashSet(StringComparer.Ordinal);
        foreach (var key in request.Values.Keys.Where(key => !knownKeys.Contains(key)))
        {
            AddError(issues, "fieldNotInPackage", $"Submitted answer '{key}' is not defined at the top level.", key);
        }

        var root = NormalizeXlsScope(mainFields, request.Values, new(StringComparer.Ordinal), request, issues);
        foreach (var field in mainFields.Where(static field => !IsXlsLocation(field) && !IsAttachmentField(field)))
        {
            root.TryAdd(field.FieldId!, null);
        }
        foreach (var section in package.Sections.Where(static section => section.Repeatable))
        {
            NormalizeXlsRepeat(section, scopes[section.SectionId!], root, request.Values, resource, issues);
        }
    }

    private static void NormalizeXlsRepeat(
        FormSectionDefinition section, FormFieldDefinition[] fields, Dictionary<string, object?> parent,
        Dictionary<string, JsonElement> values, MetadataV2Object resource, List<FormValidationIssue> issues)
    {
        var sectionId = section.SectionId!;
        if (!values.TryGetValue(sectionId, out var submitted))
        {
            if (section.MinInstances is > 0)
            {
                AddError(issues, "xlsFormRepeatCount", "Required repeat rows are missing.", sectionId);
            }
            else if (resource.SchemaFields.Any(field => !field.Nullable &&
                string.Equals(field.Name, sectionId, StringComparison.OrdinalIgnoreCase)))
            {
                values[sectionId] = JsonSerializer.SerializeToElement("[]", FormPackageJsonContext.Default.String);
            }
            return;
        }
        if (submitted.ValueKind != JsonValueKind.Array ||
            submitted.GetArrayLength() < (section.MinInstances ?? 0) ||
            submitted.GetArrayLength() > (section.MaxInstances ?? MaxRepeatRows))
        {
            AddError(issues, "xlsFormRepeatCount", "Submit an array with an allowed number of repeat rows.", sectionId);
            return;
        }

        var rows = new List<Dictionary<string, JsonElement>>();
        var fieldIds = fields.Select(static field => field.FieldId).ToHashSet(StringComparer.Ordinal);
        foreach (var item in submitted.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                AddError(issues, "xlsFormRepeatRow", "Each repeat row must be an object.", sectionId);
                continue;
            }

            var answers = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in item.EnumerateObject())
            {
                if (!fieldIds.Contains(property.Name) || !answers.TryAdd(property.Name, property.Value))
                {
                    AddError(issues, "xlsFormRepeatField", "Repeat row contains an unknown or duplicate answer.", property.Name);
                }
            }
            NormalizeXlsScope(fields, answers, new(parent, StringComparer.Ordinal), null, issues);
            foreach (var field in fields)
            {
                if (field.FieldId is { } fieldId && answers.TryGetValue(fieldId, out var answer) &&
                    !IsValidRepeatAnswer(field, answer))
                {
                    AddError(issues, "fieldValueTypeMismatch", $"Repeat answer '{fieldId}' has the wrong type.", fieldId);
                }
            }
            rows.Add(answers);
        }

        var json = JsonSerializer.Serialize(rows, FormPackageJsonContext.Default.ListDictionaryStringJsonElement);
        var target = resource.SchemaFields.FirstOrDefault(field =>
            string.Equals(field.Name, sectionId, StringComparison.OrdinalIgnoreCase));
        if (target?.Length is int limit && json.Length > limit)
        {
            AddError(issues, "fieldValueTooLong", $"Repeat answers exceed the target length of {limit}.", sectionId);
        }
        values[sectionId] = JsonSerializer.SerializeToElement(json, FormPackageJsonContext.Default.String);
    }

    private static bool IsValidRepeatAnswer(FormFieldDefinition field, JsonElement value) => value.ValueKind == JsonValueKind.Null ||
        (field.Type switch
        {
            "text" or "barcode" or "choice" => value.ValueKind == JsonValueKind.String,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "number" => TryReadFiniteDouble(value, out _) && value.ValueKind == JsonValueKind.Number,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "date" or "datetime" => value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out _),
            "multiChoice" => value.ValueKind == JsonValueKind.Array,
            _ => false
        });

    private static Dictionary<string, object?> NormalizeXlsScope(
        FormFieldDefinition[] fields, Dictionary<string, JsonElement> values, Dictionary<string, object?> context,
        FormSubmissionRequest? submission, List<FormValidationIssue> issues)
    {
        var allowed = context.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            var id = field.FieldId!;
            if (field.CalculatedExpression is not null && values.ContainsKey(id))
            {
                AddError(issues, "readOnlyFieldSubmitted", $"Calculated answer '{id}' is server-generated.", id);
            }
            var visible = field.RelevanceExpression is null ||
                EvaluateXlsCondition(field.RelevanceExpression, allowed, context, field, false, issues);
            if (IsXlsLocation(field) || IsAttachmentField(field))
            {
                ValidateXlsVirtual(field, values, submission, visible, issues);
                continue;
            }
            if (!visible)
            {
                values.Remove(id);
                allowed.Add(id);
                continue;
            }

            if (field.CalculatedExpression is { } calculation)
            {
                if (FormXlsFormExpressions.TryCompile(calculation, allowed, false, out var compiled) &&
                    FormXlsFormExpressions.TryCalculate(compiled, field.Type, context, out var calculated))
                {
                    values[id] = calculated;
                }
                else
                {
                    AddError(issues, "xlsFormCalculationFailed", $"Calculation for '{id}' failed.", id);
                }
            }

            allowed.Add(id);
            var hasAnswer = values.TryGetValue(id, out var value) && value.ValueKind != JsonValueKind.Null &&
                !(value.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(value.GetString())) &&
                !(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0);
            if (field.Required && !hasAnswer)
            {
                AddError(issues, "requiredFieldMissing", $"Required field '{id}' is missing.", id);
            }
            if (!hasAnswer)
            {
                if (IsXlsMultipleChoice(field) && value.ValueKind == JsonValueKind.Array && submission is not null)
                {
                    values[id] = JsonSerializer.SerializeToElement("[]", FormPackageJsonContext.Default.String);
                }
                continue;
            }

            context[id] = FormXlsFormExpressions.ToContextValue(value);
            if (IsXlsMultipleChoice(field))
            {
                ValidateXlsChoices(field, value, issues);
                if (submission is not null)
                {
                    values[id] = JsonSerializer.SerializeToElement(
                        JsonSerializer.Serialize(value, FormPackageJsonContext.Default.JsonElement), FormPackageJsonContext.Default.String);
                }
            }
            else if (submission is null)
            {
                ValidateSubmittedDomain(field, value, issues);
            }
            if (field.ConstraintExpression is { } constraint)
            {
                context["."] = context[id];
                if (!EvaluateXlsCondition(constraint, allowed, context, field, true, issues))
                {
                    AddError(issues, "xlsFormConstraintFailed", field.ConstraintMessage ?? $"Answer for '{id}' violates its constraint.", id);
                }
                context.Remove(".");
            }
        }
        return context;
    }

    private static bool EvaluateXlsCondition(
        string expression, IReadOnlySet<string> allowed, Dictionary<string, object?> context,
        FormFieldDefinition field, bool allowSelf, List<FormValidationIssue> issues)
    {
        if (!FormXlsFormExpressions.TryCompile(expression, allowed, allowSelf, out var compiled) ||
            !FormXlsFormExpressions.TryEvaluateBoolean(compiled, context, out var result))
        {
            AddError(issues, "xlsFormExpressionFailed", "The XLSForm expression could not be evaluated.", field.FieldId);
            return false;
        }
        return result;
    }

    private static void ValidateXlsChoices(FormFieldDefinition field, JsonElement value, List<FormValidationIssue> issues)
    {
        var codes = field.Domain?.Choices.Select(static choice => JsonElementToString(choice.Code))
            .ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (value.ValueKind != JsonValueKind.Array || codes is null ||
            value.EnumerateArray().Any(choice => choice.ValueKind != JsonValueKind.String ||
                choice.GetString() is not { } code || !codes.Contains(code) || !seen.Add(code)))
        {
            AddError(issues, "fieldValueOutsideDomain", "Choose distinct values from the field's configured choices.", field.FieldId);
        }
    }

    private static void ValidateXlsVirtual(
        FormFieldDefinition field, Dictionary<string, JsonElement> values, FormSubmissionRequest? submission,
        bool visible, List<FormValidationIssue> issues)
    {
        var supplied = IsXlsLocation(field)
            ? submission?.Geometry is not null
            : submission?.Attachments.Any(attachment => string.Equals(attachment.FieldId, field.FieldId, StringComparison.OrdinalIgnoreCase)) == true;
        if (values.ContainsKey(field.FieldId!) || (!visible && supplied))
        {
            AddError(issues, "xlsFormUnexpectedAnswer", "Send visible location and media answers in their dedicated submission properties.", field.FieldId);
        }
        else if (visible && field.Required && !supplied)
        {
            AddError(issues, "requiredFieldMissing", $"Required field '{field.FieldId}' is missing.", field.FieldId);
        }
    }
}
