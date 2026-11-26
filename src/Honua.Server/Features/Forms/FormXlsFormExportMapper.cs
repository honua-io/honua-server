// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Forms.Packages;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Forms.Authoring;

namespace Honua.Server.Features.Forms;

// Maps a published server form package onto an SDK form definition for XLSForm export.
// Field behavior XLSForm cannot carry becomes a diagnostic on the survey row the field
// would occupy, so an export never silently omits behavior. Submit, attachment, privacy
// and offline policies are server configuration, not survey content, and are not exported.
internal sealed class FormXlsFormExportMapper
{
    private const string UngroupedSectionId = "_xlsform_ungrouped_1";

    private readonly List<XlsFormDiagnostic> _diagnostics = [];

    // Survey rows start below the header; each section adds a begin and an end row (XlsFormExport layout).
    private int _row = 2;

    public static XlsFormResult<FormDefinition> ToDefinition(FormPackageVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return new FormXlsFormExportMapper().Map(version);
    }

    public static XlsFormResult<XlsFormWorkbook> ToWorkbook(FormPackageVersion version, FormDefinition definition)
    {
        if (version.Package.ExpressionLanguage != FormXlsFormImportMapper.ExpressionLanguage)
        {
            return XlsFormConverter.Export(definition);
        }

        // The published package has been validated by the server runtime. Use the
        // SDK converter for workbook structure and preserve those validated rules.
        var fields = definition.Sections.SelectMany(static section => section.Fields)
            .GroupBy(static field => field.FieldId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        var structural = definition with
        {
            Sections = definition.Sections.Select(static section => section with
            {
                Fields = section.Fields.Select(static field => field with
                {
                    RelevanceExpression = null,
                    CalculatedExpression = field.CalculatedExpression is null ? null : "1",
                    Validation = field.Validation with { ConstraintExpression = null, ConstraintMessage = null }
                }).ToArray()
            }).ToArray()
        };
        var exported = XlsFormConverter.Export(structural);
        return exported.Value is { } workbook
            ? exported with { Value = workbook with { Survey = workbook.Survey.Select(row => RestoreRules(row, fields)).ToArray() } }
            : exported;
    }

    private static XlsFormRow RestoreRules(XlsFormRow row, Dictionary<string, FormField> fields)
    {
        if (row.Cells.GetValueOrDefault("type") == "begin_group"
            || !row.Cells.TryGetValue("name", out var name)
            || !fields.TryGetValue(name, out var field))
        {
            return row;
        }

        var cells = new Dictionary<string, string>(row.Cells, StringComparer.Ordinal);
        SetRule("relevant", field.RelevanceExpression);
        SetRule("calculation", field.CalculatedExpression);
        SetRule("constraint", field.Validation.ConstraintExpression);
        SetRule("constraint_message", field.Validation.ConstraintMessage);
        return row with { Cells = cells };

        void SetRule(string column, string? expression)
        {
            if (expression is not null)
            {
                cells[column] = expression;
            }
        }
    }

    private XlsFormResult<FormDefinition> Map(FormPackageVersion version)
    {
        var package = version.Package;
        var sections = MapSections(package);
        if (_diagnostics.Count > 0)
        {
            return new XlsFormResult<FormDefinition> { Diagnostics = _diagnostics };
        }

        return new XlsFormResult<FormDefinition>
        {
            Value = new FormDefinition
            {
                FormId = version.FormId,
                Name = package.Title ?? version.FormId,
                Version = version.Version.ToString(CultureInfo.InvariantCulture),
                Description = package.Description,
                Target = new FormTarget { ServiceId = package.Target?.ServiceId, LayerId = package.Target?.LayerId },
                Sections = sections,
                Metadata = package.Metadata ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            }
        };
    }

    private List<FormSection> MapSections(FormPackageDocument package)
    {
        var fieldsById = package.Fields
            .Where(static field => !string.IsNullOrWhiteSpace(field.FieldId))
            .GroupBy(static field => field.FieldId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);
        var sections = package.Sections.Select(section => MapSection(section, fieldsById)).ToList();
        var placed = package.Sections.SelectMany(static section => section.FieldIds).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unplaced = package.Fields.Where(field => !placed.Contains(field.FieldId ?? string.Empty)).ToArray();
        if (unplaced.Length > 0)
        {
            var ungrouped = new FormSectionDefinition { SectionId = UngroupedSectionId, Label = "Questions" };
            sections.Add(MapSection(ungrouped, unplaced));
        }

        return sections;
    }

    private FormSection MapSection(FormSectionDefinition section, Dictionary<string, FormFieldDefinition> fieldsById)
    {
        foreach (var missing in section.FieldIds.Where(id => !fieldsById.ContainsKey(id)))
        {
            Report("name", $"Section '{section.SectionId}' lists unknown field '{missing}'.");
        }

        return MapSection(section, section.FieldIds.Where(fieldsById.ContainsKey).Select(id => fieldsById[id]).ToArray());
    }

    private FormSection MapSection(FormSectionDefinition section, FormFieldDefinition[] fields)
    {
        var sectionId = section.SectionId ?? string.Empty;
        if (section.Repeatable || section.MinInstances is not null || section.MaxInstances is not null)
        {
            Report("type", $"Repeatable section '{sectionId}' has no XLSForm export yet.");
        }

        _row++;
        var mapped = fields.Select(MapField).ToArray();
        _row++;
        return new FormSection
        {
            SectionId = sectionId,
            Label = section.Label ?? sectionId,
            Description = section.Description,
            Fields = mapped
        };
    }

    private FormField MapField(FormFieldDefinition field)
    {
        var fieldId = field.FieldId ?? string.Empty;
        var type = ResolveType(field);
        CheckPortable(field);
        _row++;
        return new FormField
        {
            FieldId = fieldId,
            Label = field.Label ?? fieldId,
            Type = type,
            Required = field.Required,
            HelpText = field.Hint,
            Choices = Choices(field),
            RelevanceExpression = field.RelevanceExpression,
            CalculatedExpression = field.CalculatedExpression,
            Validation = new FieldValidationRule
            {
                ConstraintExpression = Constraint(field),
                ConstraintMessage = field.ConstraintMessage
            }
        };
    }

    private FormFieldType ResolveType(FormFieldDefinition field)
    {
        if (field.CalculatedExpression is not null)
        {
            return FormFieldType.Calculated;
        }

        FormFieldType? type = field.Type?.ToLowerInvariant() switch
        {
            "text" or "string" => FormFieldType.Text,
            "barcode" => FormFieldType.Barcode,
            "choice" => FormFieldType.SingleChoice,
            "integer" or "int" or "number" or "decimal" or "double" => FormFieldType.Numeric,
            "date" => FormFieldType.Date,
            "datetime" => FormFieldType.DateTime,
            _ => null
        };
        if (type is null)
        {
            Report("type", $"Field '{field.FieldId}' type '{field.Type}' has no XLSForm question type.");
        }

        return type ?? FormFieldType.Text;
    }

    private void CheckPortable(FormFieldDefinition field)
    {
        if (!string.Equals(field.TargetField, field.FieldId, StringComparison.OrdinalIgnoreCase))
        {
            Report("name", $"Field '{field.FieldId}' binds to attribute '{field.TargetField}'; XLSForm binds each question to the attribute of the same name.");
        }

        if (field.Private || (field.ReadOnly && field.CalculatedExpression is null) || field.DefaultValue is not null)
        {
            Report("type", $"Field '{field.FieldId}' privacy, read-only or default value settings have no XLSForm export yet.");
        }

        if (field.Validation.Length > 0 || field.Visibility is not null)
        {
            Report("constraint", $"Field '{field.FieldId}' validation or visibility rules have no XLSForm export; use constraint and relevant expressions.");
        }

        if (!IsChoiceField(field) && field.Domain is not null)
        {
            Report("type", $"Field '{field.FieldId}' domain can be exported only as the coded-value choices of a choice field.");
        }
    }

    private List<FieldChoice> Choices(FormFieldDefinition field)
    {
        if (!IsChoiceField(field))
        {
            return [];
        }

        if (field.Domain is not { Choices.Length: > 0 } domain
            || !string.Equals(domain.Type, "codedValue", StringComparison.OrdinalIgnoreCase))
        {
            Report("type", $"Choice field '{field.FieldId}' needs a coded-value domain with at least one choice.");
            return [];
        }

        return domain.Choices.Select(static choice => new FieldChoice { Value = ChoiceValue(choice.Code), Label = choice.Label }).ToList();
    }

    private static string? Constraint(FormFieldDefinition field)
        => field.ConstraintExpression
            ?? (IsIntegerField(field) ? FormXlsFormImportMapper.IntegerConstraint : null);

    private static bool IsChoiceField(FormFieldDefinition field)
        => string.Equals(field.Type, "choice", StringComparison.OrdinalIgnoreCase) && field.CalculatedExpression is null;

    private static bool IsIntegerField(FormFieldDefinition field)
        => string.Equals(field.Type, "integer", StringComparison.OrdinalIgnoreCase)
            || string.Equals(field.Type, "int", StringComparison.OrdinalIgnoreCase);

    private static string ChoiceValue(JsonElement code)
        => code.ValueKind == JsonValueKind.String ? code.GetString() ?? string.Empty : code.GetRawText();

    private void Report(string column, string message)
        => _diagnostics.Add(new XlsFormDiagnostic { Sheet = "survey", RowNumber = _row, Column = column, Message = message });
}
