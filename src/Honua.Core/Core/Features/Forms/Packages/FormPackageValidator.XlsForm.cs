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

        var root = NormalizeXlsScope(mainFields, new(request.Values, new(StringComparer.Ordinal), issues) { Submission = request });
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

        NormalizeXlsScope(repeat.Fields, new(answers, new(context.Parent, StringComparer.Ordinal), context.Issues));
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
        List<FormValidationIssue> Issues)
    {
        public FormSubmissionRequest? Submission { get; init; }

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
