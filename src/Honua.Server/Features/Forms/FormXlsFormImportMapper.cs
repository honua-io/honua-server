// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Forms.Packages;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Forms.Authoring;

namespace Honua.Server.Features.Forms;

// Maps an imported SDK form definition onto a server form package for one target layer.
// Every question the package cannot represent becomes a diagnostic on its survey row;
// the mapping never returns a package that silently omits a question.
internal sealed class FormXlsFormImportMapper
{
    internal const string ExpressionLanguage = "xlsform";
    internal const string ProvenanceSource = "xlsform";

    // The SDK importer adds this constraint to integer questions; an integer target attribute enforces it already.
    internal const string IntegerConstraint = "floor(.) = .";

    private readonly XlsFormWorkbook _workbook;
    private readonly FormXlsFormTarget _target;
    private readonly List<XlsFormDiagnostic> _diagnostics = [];

    private FormXlsFormImportMapper(XlsFormWorkbook workbook, FormXlsFormTarget target)
    {
        _workbook = workbook;
        _target = target;
    }

    public static XlsFormResult<FormPackageDocument> ToPackage(XlsFormWorkbook workbook, FormXlsFormTarget target)
    {
        ArgumentNullException.ThrowIfNull(workbook);
        ArgumentNullException.ThrowIfNull(target);

        // The SDK converter validates structural columns using its own expression subset.
        // Supply structural placeholders and validate the original rules against the server runtime.
        var structuralWorkbook = new XlsFormWorkbook
        {
            Choices = workbook.Choices,
            Settings = workbook.Settings,
            Survey = workbook.Survey.Select(static row =>
            {
                var cells = new Dictionary<string, string>(row.Cells, StringComparer.Ordinal);
                cells.Remove("relevant");
                cells.Remove("constraint");
                cells.Remove("constraint_message");
                cells.Remove("default");
                if (cells.TryGetValue("calculation", out var calculation) && !string.IsNullOrWhiteSpace(calculation))
                {
                    cells["calculation"] = "1";
                }

                return new XlsFormRow { RowNumber = row.RowNumber, Cells = cells };
            }).ToArray()
        };
        var imported = XlsFormConverter.Import(structuralWorkbook);
        return imported.Value is { } form
            ? new FormXlsFormImportMapper(workbook, target).Map(form)
            : new XlsFormResult<FormPackageDocument> { Diagnostics = imported.Diagnostics };
    }

    private XlsFormResult<FormPackageDocument> Map(FormDefinition form)
    {
        var sections = new List<FormSectionDefinition>();
        var fields = new List<FormFieldDefinition>();
        ValidateSurveyStructure();
        foreach (var section in form.Sections)
        {
            if (section.Repeatable && AttributeType(section.SectionId) != MetadataV2FieldType.String)
            {
                Report(section.SectionId, "type", "A repeat group requires a text attribute with the same name on the target layer.");
                continue;
            }

            fields.AddRange(section.Fields.Select(field => MapField(field, section)).OfType<FormFieldDefinition>());
            sections.Add(new FormSectionDefinition
            {
                SectionId = section.SectionId,
                Label = section.Label,
                Description = section.Description,
                Repeatable = section.Repeatable,
                FieldIds = section.Fields.Select(static field => field.FieldId).ToArray()
            });
        }

        ValidateFormIdentifier(form.FormId);
        var package = CreatePackage(form, sections, fields);
        ValidateRuntimeExpressions(package);

        return _diagnostics.Count > 0
            ? new XlsFormResult<FormPackageDocument> { Diagnostics = _diagnostics }
            : new XlsFormResult<FormPackageDocument> { Value = package };
    }

    private void ValidateFormIdentifier(string formId)
    {
        if (!FormPackageIdentifier.IsAcceptable(formId))
        {
            _diagnostics.Add(new XlsFormDiagnostic
            {
                Sheet = "settings",
                RowNumber = 2,
                Column = "form_id",
                Message = "The form identifier must be 1-128 ASCII letters, digits, hyphens or underscores, and must not be a reserved route segment."
            });
        }
    }

    private void ValidateRuntimeExpressions(FormPackageDocument package)
    {
        if (_diagnostics.Count == 0 && package.ExpressionLanguage == ExpressionLanguage)
        {
            var issues = new List<FormValidationIssue>();
            FormPackageValidator.ValidateXlsExpressions(package, issues);
            foreach (var issue in issues)
            {
                var column = issue.Path switch
                {
                    "relevanceExpression" => "relevant",
                    "constraintExpression" => "constraint",
                    _ => "calculation"
                };
                Report(issue.FieldId ?? string.Empty, column, issue.Message);
            }
        }
    }

    private FormPackageDocument CreatePackage(
        FormDefinition form,
        List<FormSectionDefinition> sections,
        List<FormFieldDefinition> fields) => new()
        {
            FormId = form.FormId,
            Title = form.Name,
            ExpressionLanguage = fields.Any(NeedsRuntime) || sections.Any(static section => section.Repeatable)
                ? ExpressionLanguage
                : null,
            Target = _target.Definition,
            Sections = sections.ToArray(),
            Fields = fields.ToArray(),
            SubmitPolicy = new FormSubmitPolicy
            {
                AllowedOperations = [FormSubmissionOperations.Create],
                RequiresGeometry = _target.HasGeometry && !fields.Any(static field => field.Type == "location"),
                AllowAttachments = fields.Any(static field => field.Type == "media")
            },
            AttachmentPolicy = new FormAttachmentPolicy
            {
                Enabled = fields.Any(static field => field.Type == "media"),
                Fields = fields.Where(static field => field.Type == "media").Select(field =>
                    new FormFieldAttachmentPolicy { FieldId = field.FieldId, MaxCount = 1, AllowedContentTypes = MediaTypes(Cell(field.FieldId!, "type")) }).ToArray()
            },
            Provenance = new FormProvenanceRef { Source = ProvenanceSource, SourceVersion = form.Version }
        };

    private FormFieldDefinition? MapField(FormField field, FormSection section)
    {
        var type = ResolveType(field, section);
        if (type is null)
        {
            return null;
        }

        if (section.Repeatable && type is "media" or "location")
        {
            Report(field.FieldId, "type", "Repeat groups cannot contain media or geopoint questions.");
            return null;
        }

        if (type == "location" && !_target.HasGeometry)
        {
            Report(field.FieldId, "type", "A geopoint question requires a point geometry target layer.");
            return null;
        }

        return CreateField(field, section, type);
    }

    private FormFieldDefinition CreateField(FormField field, FormSection section, string type)
    {
        var constraint = OptionalCell(field.FieldId, "constraint");
        if (Cell(field.FieldId, "type") == "integer")
        {
            constraint = constraint is null ? IntegerConstraint : $"{IntegerConstraint} and ({constraint})";
        }

        return new FormFieldDefinition
        {
            FieldId = field.FieldId,
            Label = field.Label,
            Hint = field.HelpText,
            Type = type,
            TargetField = section.Repeatable || type is "media" or "location" ? null : field.FieldId,
            SectionId = section.SectionId,
            Required = field.Required,
            ReadOnly = field.Type == FormFieldType.Calculated,
            Domain = ChoiceDomain(field),
            DefaultValue = ParseDefault(field, type),
            RelevanceExpression = OptionalCell(field.FieldId, "relevant"),
            ConstraintExpression = section.Repeatable ? constraint : TargetConstraint(type, constraint),
            ConstraintMessage = OptionalCell(field.FieldId, "constraint_message"),
            CalculatedExpression = OptionalCell(field.FieldId, "calculation")
        };
    }

    private JsonElement? ParseDefault(FormField field, string type)
    {
        var raw = SurveyRow(field.FieldId)?.Cells.GetValueOrDefault("default");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim();
        var context = FormXlsFormJsonContext.Default;
        JsonElement? parsed = !string.IsNullOrWhiteSpace(Cell(field.FieldId, "calculation")) ||
            value.Contains("${", StringComparison.Ordinal)
            ? null
            : type switch
            {
                "text" or "barcode" => JsonSerializer.SerializeToElement(raw, context.String),
                "choice" when HasChoice(field, value) => JsonSerializer.SerializeToElement(value, context.String),
                "multiChoice" => ParseMultipleChoiceDefault(field, value),
                "integer" or "number" when type == "integer" || Cell(field.FieldId, "type") == "integer" =>
                    long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)
                        ? JsonSerializer.SerializeToElement(integer, context.Int64) : null,
                "number" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
                    double.IsFinite(number) => JsonSerializer.SerializeToElement(number, context.Double),
                "boolean" when bool.TryParse(value, out var boolean) => JsonSerializer.SerializeToElement(boolean, context.Boolean),
                "date" when DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) =>
                    JsonSerializer.SerializeToElement(value, context.String),
                "datetime" when DateTimeOffset.TryParseExact(value,
                    ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"],
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp) =>
                    JsonSerializer.SerializeToElement(timestamp, context.DateTimeOffset),
                _ => null
            };

        if (parsed is null)
        {
            Report(field.FieldId, "default", $"Expected a supported literal default for the '{type}' question.");
        }

        return parsed;
    }

    private static JsonElement? ParseMultipleChoiceDefault(FormField field, string value)
    {
        var codes = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return codes.All(code => HasChoice(field, code)) && codes.Distinct(StringComparer.Ordinal).Count() == codes.Length
            ? JsonSerializer.SerializeToElement(codes, FormXlsFormJsonContext.Default.StringArray)
            : null;
    }

    private static bool HasChoice(FormField field, string code)
        => field.Choices.Any(choice => string.Equals(choice.Value, code, StringComparison.Ordinal));

    private string? ResolveType(FormField field, FormSection section) => field.Type switch
    {
        FormFieldType.Text => "text",
        FormFieldType.Barcode => "barcode",
        FormFieldType.SingleChoice => "choice",
        FormFieldType.MultipleChoice => "multiChoice",
        FormFieldType.Location => "location",
        FormFieldType.Photo or FormFieldType.Video or FormFieldType.Audio or FormFieldType.File => "media",
        FormFieldType.Numeric => (section.Repeatable ? Cell(field.FieldId, "type") == "integer" :
            AttributeType(field.FieldId) is MetadataV2FieldType.Integer or MetadataV2FieldType.BigInteger)
            ? "integer"
            : "number",
        FormFieldType.Date => "date",
        FormFieldType.DateTime => "datetime",
        FormFieldType.YesNo => "boolean",
        FormFieldType.Calculated => section.Repeatable ? "text" : CalculatedType(field.FieldId),
        _ => ReportUnsupported(field.FieldId)
    };

    private string? CalculatedType(string fieldId)
    {
        var type = AttributeType(fieldId) switch
        {
            MetadataV2FieldType.String => "text",
            MetadataV2FieldType.Integer or MetadataV2FieldType.BigInteger => "integer",
            MetadataV2FieldType.Double or MetadataV2FieldType.Float => "number",
            MetadataV2FieldType.Boolean => "boolean",
            MetadataV2FieldType.Date => "date",
            MetadataV2FieldType.DateTime => "datetime",
            _ => null
        };
        if (type is null)
        {
            Report(fieldId, "calculation", $"Calculated question '{fieldId}' needs a text, number, boolean, date or date-time attribute named '{fieldId}' on the target layer.");
        }

        return type;
    }

    private static string[] MediaTypes(string type) => type switch
    {
        "image" => ["image/*"],
        "video" => ["video/*"],
        "audio" => ["audio/*"],
        _ => []
    };

    private void ValidateSurveyStructure()
    {
        var groups = new Stack<bool>();
        foreach (var row in _workbook.Survey)
        {
            var type = row.Cells.GetValueOrDefault("type")?.Trim();
            if (type is "end_repeat" or "end_group")
            {
                groups.TryPop(out _);
                continue;
            }

            if (type is not ("begin_repeat" or "begin_group"))
            {
                continue;
            }

            var repeat = type == "begin_repeat";
            var name = row.Cells.GetValueOrDefault("name")?.Trim() ?? string.Empty;
            if (groups.Contains(true) || repeat && groups.Count > 0)
            {
                Report(name, "type", "Repeat groups must be top-level and may contain only questions.");
            }

            foreach (var column in new[] { "relevant", "constraint", "calculation", "required", "repeat_count", "default" })
            {
                if (!string.IsNullOrWhiteSpace(row.Cells.GetValueOrDefault(column)))
                {
                    Report(name, column, "Rules on group and repeat rows are unsupported.");
                }
            }

            groups.Push(repeat);
        }
    }

    private MetadataV2FieldType? AttributeType(string fieldId)
        => _target.Attributes.TryGetValue(fieldId, out var type) ? type : null;

    private string? ReportUnsupported(string fieldId)
    {
        Report(fieldId, "type", $"Question type '{Cell(fieldId, "type")}' cannot be represented by a Honua form package.");
        return null;
    }

    private void Report(string name, string column, string message)
        => _diagnostics.Add(new XlsFormDiagnostic
        {
            Sheet = "survey",
            RowNumber = SurveyRow(name)?.RowNumber ?? 1,
            Column = column,
            Message = message
        });

    private string Cell(string name, string column)
        => SurveyRow(name)?.Cells.GetValueOrDefault(column)?.Trim() ?? string.Empty;

    private string? OptionalCell(string name, string column)
        => Cell(name, column) is { Length: > 0 } value ? value : null;

    private XlsFormRow? SurveyRow(string name)
        => _workbook.Survey.FirstOrDefault(row =>
            string.Equals(row.Cells.GetValueOrDefault("name")?.Trim(), name, StringComparison.Ordinal));

    private static FormFieldDomainDefinition? ChoiceDomain(FormField field)
        => field.Type is not (FormFieldType.SingleChoice or FormFieldType.MultipleChoice)
            ? null
            : new FormFieldDomainDefinition
            {
                Type = "codedValue",
                Choices = field.Choices.Select(static choice => new FormDomainChoice
                {
                    Code = JsonSerializer.SerializeToElement(choice.Value, FormXlsFormJsonContext.Default.String),
                    Label = choice.Label
                }).ToArray()
            };

    private static string? TargetConstraint(string type, string? constraint)
        => type == "integer" && string.Equals(constraint, IntegerConstraint, StringComparison.Ordinal) ? null : constraint;

    private static bool NeedsRuntime(FormFieldDefinition field)
        => field.Type is "multiChoice" or "media" or "location" || field.RelevanceExpression is not null || field.ConstraintExpression is not null || field.CalculatedExpression is not null;
}
