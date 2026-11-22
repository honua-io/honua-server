// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

    public static XlsFormResult<FormPackageDocument> ToPackage(
        FormDefinition form,
        XlsFormWorkbook workbook,
        FormXlsFormTarget target)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(workbook);
        ArgumentNullException.ThrowIfNull(target);
        return new FormXlsFormImportMapper(workbook, target).Map(form);
    }

    private XlsFormResult<FormPackageDocument> Map(FormDefinition form)
    {
        var sections = new List<FormSectionDefinition>();
        var fields = new List<FormFieldDefinition>();
        foreach (var section in form.Sections)
        {
            if (section.Repeatable)
            {
                Report(section.SectionId, "type", "Repeat groups cannot be represented by a Honua form package.");
                continue;
            }

            fields.AddRange(section.Fields.Select(field => MapField(field, section.SectionId)).OfType<FormFieldDefinition>());
            sections.Add(new FormSectionDefinition
            {
                SectionId = section.SectionId,
                Label = section.Label,
                Description = section.Description,
                FieldIds = section.Fields.Select(static field => field.FieldId).ToArray()
            });
        }

        if (!FormPackageIdentifier.IsAcceptable(form.FormId))
        {
            _diagnostics.Add(new XlsFormDiagnostic
            {
                Sheet = "settings",
                RowNumber = 2,
                Column = "form_id",
                Message = "The form identifier must be 1-128 ASCII letters, digits, hyphens or underscores, and must not be a reserved route segment."
            });
        }

        return _diagnostics.Count > 0
            ? new XlsFormResult<FormPackageDocument> { Diagnostics = _diagnostics }
            : new XlsFormResult<FormPackageDocument> { Value = CreatePackage(form, sections, fields) };
    }

    private FormPackageDocument CreatePackage(
        FormDefinition form,
        List<FormSectionDefinition> sections,
        List<FormFieldDefinition> fields) => new()
        {
            FormId = form.FormId,
            Title = form.Name,
            ExpressionLanguage = fields.Any(HasExpression) ? ExpressionLanguage : null,
            Target = _target.Definition,
            Sections = sections.ToArray(),
            Fields = fields.ToArray(),
            SubmitPolicy = new FormSubmitPolicy
            {
                AllowedOperations = [FormSubmissionOperations.Create],
                RequiresGeometry = _target.HasGeometry,
                AllowAttachments = false
            },
            Provenance = new FormProvenanceRef { Source = ProvenanceSource, SourceVersion = form.Version }
        };

    private FormFieldDefinition? MapField(FormField field, string sectionId)
    {
        var type = ResolveType(field);
        if (type is null)
        {
            return null;
        }

        return new FormFieldDefinition
        {
            FieldId = field.FieldId,
            Label = field.Label,
            Hint = field.HelpText,
            Type = type,
            TargetField = field.FieldId,
            SectionId = sectionId,
            Required = field.Required,
            ReadOnly = field.Type == FormFieldType.Calculated,
            Domain = ChoiceDomain(field),
            RelevanceExpression = field.RelevanceExpression,
            ConstraintExpression = TargetConstraint(type, field.Validation.ConstraintExpression),
            ConstraintMessage = field.Validation.ConstraintMessage,
            CalculatedExpression = field.CalculatedExpression
        };
    }

    private string? ResolveType(FormField field) => field.Type switch
    {
        FormFieldType.Text => "text",
        FormFieldType.Barcode => "barcode",
        FormFieldType.SingleChoice => "choice",
        FormFieldType.Numeric => AttributeType(field.FieldId) is MetadataV2FieldType.Integer or MetadataV2FieldType.BigInteger
            ? "integer"
            : "number",
        FormFieldType.Date => "date",
        FormFieldType.DateTime => "datetime",
        FormFieldType.YesNo => "boolean",
        FormFieldType.Calculated => CalculatedType(field.FieldId),
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

    private XlsFormRow? SurveyRow(string name)
        => _workbook.Survey.FirstOrDefault(row =>
            string.Equals(row.Cells.GetValueOrDefault("name")?.Trim(), name, StringComparison.Ordinal));

    private static FormFieldDomainDefinition? ChoiceDomain(FormField field)
        => field.Type != FormFieldType.SingleChoice
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

    private static bool HasExpression(FormFieldDefinition field)
        => field.RelevanceExpression is not null || field.ConstraintExpression is not null || field.CalculatedExpression is not null;
}
