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

    private sealed record XlsSubmissionTarget(FormSubmissionRequest Request, MetadataV2Object Resource);

    private static void NormalizeXlsSubmission(
        FormPackageDocument package, XlsSubmissionTarget target, List<FormValidationIssue> issues)
    {
        var request = target.Request;
        var scopes = GetXlsScopes(package);
        var mainFields = scopes[string.Empty];
        var knownKeys = mainFields.Select(static field => field.FieldId)
            .Concat(scopes.Keys.Where(static key => key.Length > 0)).ToHashSet(StringComparer.Ordinal);
        foreach (var key in request.Values.Keys.Where(key => !knownKeys.Contains(key)))
        {
            AddError(issues, "fieldNotInPackage", $"Submitted answer '{key}' is not defined at the top level.", key);
        }

        var root = NormalizeXlsScope(mainFields, new(request.Values, new(StringComparer.Ordinal), request, issues));
        foreach (var field in mainFields.Where(static field => !IsXlsLocation(field) && !IsAttachmentField(field)))
        {
            root.TryAdd(field.FieldId!, null);
        }
        foreach (var section in package.Sections.Where(static section => section.Repeatable))
        {
            NormalizeXlsRepeat(section, scopes[section.SectionId!], new(root, target, issues));
        }
    }

    private sealed record XlsRepeatContext(
        Dictionary<string, object?> Parent, XlsSubmissionTarget Target, List<FormValidationIssue> Issues);

    private static void NormalizeXlsRepeat(
        FormSectionDefinition section, FormFieldDefinition[] fields, XlsRepeatContext context)
    {
        var sectionId = section.SectionId!;
        var values = context.Target.Request.Values;
        var targetField = context.Target.Resource.SchemaFields.FirstOrDefault(field =>
            string.Equals(field.Name, sectionId, StringComparison.OrdinalIgnoreCase));
        if (!values.TryGetValue(sectionId, out var submitted))
        {
            ApplyMissingXlsRepeat(section, targetField, context);
            return;
        }
        if (submitted.ValueKind != JsonValueKind.Array ||
            submitted.GetArrayLength() < (section.MinInstances ?? 0) ||
            submitted.GetArrayLength() > (section.MaxInstances ?? MaxRepeatRows))
        {
            AddError(context.Issues, "xlsFormRepeatCount", "Submit an array with an allowed number of repeat rows.", sectionId);
            return;
        }

        var rows = ReadXlsRepeatRows(submitted, new(sectionId, fields), context);
        var json = JsonSerializer.Serialize(rows, FormPackageJsonContext.Default.ListDictionaryStringJsonElement);
        if (targetField?.Length is int limit && json.Length > limit)
        {
            AddError(context.Issues, "fieldValueTooLong", $"Repeat answers exceed the target length of {limit}.", sectionId);
        }
        values[sectionId] = JsonSerializer.SerializeToElement(json, FormPackageJsonContext.Default.String);
    }

    private static void ApplyMissingXlsRepeat(
        FormSectionDefinition section, MetadataV2Field? targetField, XlsRepeatContext context)
    {
        if (section.MinInstances is > 0)
        {
            AddError(context.Issues, "xlsFormRepeatCount", "Required repeat rows are missing.", section.SectionId);
        }
        else if (targetField is { Nullable: false })
        {
            context.Target.Request.Values[section.SectionId!] =
                JsonSerializer.SerializeToElement("[]", FormPackageJsonContext.Default.String);
        }
    }

    private sealed record XlsRepeatDefinition(string SectionId, FormFieldDefinition[] Fields)
    {
        public HashSet<string?> FieldIds { get; } =
            Fields.Select(static field => field.FieldId).ToHashSet(StringComparer.Ordinal);
    }

    private static List<Dictionary<string, JsonElement>> ReadXlsRepeatRows(
        JsonElement submitted, XlsRepeatDefinition repeat, XlsRepeatContext context)
    {
        var rows = new List<Dictionary<string, JsonElement>>();
        foreach (var item in submitted.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                AddError(context.Issues, "xlsFormRepeatRow", "Each repeat row must be an object.", repeat.SectionId);
                continue;
            }
            rows.Add(ReadXlsRepeatRow(item, repeat, context));
        }
        return rows;
    }

    private static Dictionary<string, JsonElement> ReadXlsRepeatRow(
        JsonElement item, XlsRepeatDefinition repeat, XlsRepeatContext context)
    {
        var answers = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in item.EnumerateObject())
        {
            if (!repeat.FieldIds.Contains(property.Name) || !answers.TryAdd(property.Name, property.Value))
            {
                AddError(context.Issues, "xlsFormRepeatField", "Repeat row contains an unknown or duplicate answer.", property.Name);
            }
        }

        NormalizeXlsScope(repeat.Fields, new(answers, new(context.Parent, StringComparer.Ordinal), null, context.Issues));
        foreach (var field in repeat.Fields)
        {
            if (field.FieldId is { } fieldId && answers.TryGetValue(fieldId, out var answer) &&
                !IsValidRepeatAnswer(field, answer))
            {
                AddError(context.Issues, "fieldValueTypeMismatch", $"Repeat answer '{fieldId}' has the wrong type.", fieldId);
            }
        }
        return answers;
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

    private sealed record XlsAnswerScope(
        Dictionary<string, JsonElement> Values,
        Dictionary<string, object?> Context,
        FormSubmissionRequest? Submission,
        List<FormValidationIssue> Issues)
    {
        public HashSet<string> Allowed { get; } = Context.Keys.ToHashSet(StringComparer.Ordinal);
    }

    private static Dictionary<string, object?> NormalizeXlsScope(FormFieldDefinition[] fields, XlsAnswerScope scope)
    {
        foreach (var field in fields)
        {
            NormalizeXlsAnswer(field, scope);
        }
        return scope.Context;
    }

    private static void NormalizeXlsAnswer(FormFieldDefinition field, XlsAnswerScope scope)
    {
        var values = scope.Values;
        var issues = scope.Issues;
        var allowed = scope.Allowed;
        var id = field.FieldId!;
        if (field.CalculatedExpression is not null && values.ContainsKey(id))
        {
            AddError(issues, "readOnlyFieldSubmitted", $"Calculated answer '{id}' is server-generated.", id);
        }
        var visible = field.RelevanceExpression is null ||
            EvaluateXlsCondition(FormXlsFormExpressions.Compile(field.RelevanceExpression, scope.Allowed), scope, field);
        if (IsXlsLocation(field) || IsAttachmentField(field))
        {
            var supplied = HasXlsVirtualAnswer(field, scope.Submission);
            if (values.ContainsKey(id) || (!visible && supplied))
            {
                AddError(issues, "xlsFormUnexpectedAnswer", "Send visible location and media answers in their dedicated submission properties.", id);
            }
            else if (visible && field.Required && !supplied)
            {
                AddError(issues, "requiredFieldMissing", $"Required field '{id}' is missing.", id);
            }
            return;
        }
        if (!visible)
        {
            RemoveHiddenXlsAnswer(field, scope);
            allowed.Add(id);
            return;
        }

        ApplyXlsCalculation(field, scope);
        CompleteXlsAnswer(field, scope);
    }

    private static void RemoveHiddenXlsAnswer(FormFieldDefinition field, XlsAnswerScope scope)
    {
        var id = field.FieldId!;
        if (scope.Submission is { } submission && OperationEquals(submission.Operation, FormSubmissionOperations.Update) &&
            !string.IsNullOrEmpty(field.TargetField) && (!field.ReadOnly || field.CalculatedExpression is not null))
        {
            // Attribute updates are partial, so omission would retain a now-hidden answer.
            scope.Values[id] = JsonElement.Parse("null");
            return;
        }

        scope.Values.Remove(id);
    }

    private static void ApplyXlsCalculation(FormFieldDefinition field, XlsAnswerScope scope)
    {
        var id = field.FieldId!;
        if (field.CalculatedExpression is { } calculation)
        {
            if (FormXlsFormExpressions.Compile(calculation, scope.Allowed) is { } compiled &&
                FormXlsFormExpressions.Calculate(compiled, field.Type, scope.Context) is { } calculated)
            {
                scope.Values[id] = calculated;
            }
            else
            {
                AddError(scope.Issues, "xlsFormCalculationFailed", $"Calculation for '{id}' failed.", id);
            }
        }
    }

    private static void CompleteXlsAnswer(FormFieldDefinition field, XlsAnswerScope scope)
    {
        var id = field.FieldId!;
        var values = scope.Values;
        var issues = scope.Issues;
        scope.Allowed.Add(id);
        var hasAnswer = values.TryGetValue(id, out var value) && value.ValueKind != JsonValueKind.Null &&
            !(value.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(value.GetString())) &&
            !(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0);
        if (field.Required && !hasAnswer)
        {
            AddError(issues, "requiredFieldMissing", $"Required field '{id}' is missing.", id);
        }
        if (!hasAnswer)
        {
            if (IsXlsMultipleChoice(field) && value.ValueKind == JsonValueKind.Array && scope.Submission is not null)
            {
                values[id] = JsonSerializer.SerializeToElement("[]", FormPackageJsonContext.Default.String);
            }
            return;
        }

        ApplyXlsAnswer(field, value, scope);
    }

    private static void ApplyXlsAnswer(FormFieldDefinition field, JsonElement value, XlsAnswerScope scope)
    {
        var id = field.FieldId!;
        var context = scope.Context;
        context[id] = FormXlsFormExpressions.ToContextValue(value);
        if (IsXlsMultipleChoice(field))
        {
            ValidateXlsChoices(field, value, scope.Issues);
            if (scope.Submission is not null)
            {
                scope.Values[id] = JsonSerializer.SerializeToElement(
                    JsonSerializer.Serialize(value, FormPackageJsonContext.Default.JsonElement), FormPackageJsonContext.Default.String);
            }
        }
        else if (scope.Submission is null)
        {
            ValidateSubmittedDomain(field, value, scope.Issues);
        }
        if (field.ConstraintExpression is { } constraint)
        {
            context["."] = context[id];
            if (!EvaluateXlsCondition(FormXlsFormExpressions.CompileConstraint(constraint, scope.Allowed), scope, field))
            {
                AddError(scope.Issues, "xlsFormConstraintFailed", field.ConstraintMessage ?? $"Answer for '{id}' violates its constraint.", id);
            }
            context.Remove(".");
        }
    }

    private static bool EvaluateXlsCondition(
        string? compiled, XlsAnswerScope scope, FormFieldDefinition field)
    {
        if (compiled is null || FormXlsFormExpressions.EvaluateBoolean(compiled, scope.Context) is not { } result)
        {
            AddError(scope.Issues, "xlsFormExpressionFailed", "The XLSForm expression could not be evaluated.", field.FieldId);
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

    private static bool HasXlsVirtualAnswer(FormFieldDefinition field, FormSubmissionRequest? submission)
        => IsXlsLocation(field)
            ? submission?.Geometry is not null
            : submission?.Attachments.Any(attachment =>
                string.Equals(attachment.FieldId, field.FieldId, StringComparison.OrdinalIgnoreCase)) == true;
}
