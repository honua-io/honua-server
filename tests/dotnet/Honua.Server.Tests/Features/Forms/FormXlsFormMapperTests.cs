// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Forms.Packages;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Forms.Authoring;
using Honua.Server.Features.Forms;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Forms;

public sealed class FormXlsFormMapperTests
{
    private static readonly FormXlsFormTarget Target = new()
    {
        Definition = new FormTargetDefinition { ServiceId = "inspections", LayerId = 0 },
        Attributes = new Dictionary<string, MetadataV2FieldType>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = MetadataV2FieldType.String,
            ["status"] = MetadataV2FieldType.String,
            ["count"] = MetadataV2FieldType.Integer,
            ["depth"] = MetadataV2FieldType.Double,
            ["visited"] = MetadataV2FieldType.Date,
            ["total"] = MetadataV2FieldType.Integer,
            ["rounds"] = MetadataV2FieldType.String
        },
        HasGeometry = true
    };

    [UnitTest]
    public void ToPackage_SupportedQuestions_UseTargetAttributeTypes()
    {
        var package = Import(
            Row(2, "text", "name", "required", "yes"),
            Row(3, "select_one status", "status"),
            Row(4, "integer", "count"),
            Row(5, "decimal", "depth"),
            Row(6, "date", "visited"));

        package.Fields.Select(static field => (field.FieldId, field.Type)).Should().Equal(
            ("name", "text"), ("status", "choice"), ("count", "integer"), ("depth", "number"), ("visited", "date"));
        package.Fields.Should().OnlyContain(field => field.TargetField == field.FieldId && field.SectionId == "_xlsform_ungrouped_1");
        package.Fields.Single(static field => field.FieldId == "name").Required.Should().BeTrue();
        package.Fields.Single(static field => field.FieldId == "count").ConstraintExpression.Should().BeNull();
        package.Fields.Single(static field => field.FieldId == "status").Domain!.Choices
            .Select(static choice => choice.Code.GetString()).Should().Equal("open", "closed");
        package.ExpressionLanguage.Should().BeNull();
        package.Sections.Should().ContainSingle().Which.FieldIds.Should().Equal("name", "status", "count", "depth", "visited");
        package.SubmitPolicy.AllowAttachments.Should().BeFalse();
        package.Provenance!.Source.Should().Be("xlsform");
    }

    [UnitTest]
    public void ToPackage_Expressions_AreCarriedUnderTheXlsFormLanguage()
    {
        var package = Import(
            Row(2, "integer", "count", "constraint", ". > 0", "constraint_message", "Positive"),
            Row(3, "text", "name", "relevant", "${count} > 1"),
            Row(4, "calculate", "total", "calculation", "${count} * 2"));

        package.ExpressionLanguage.Should().Be("xlsform");
        var count = package.Fields.Single(static field => field.FieldId == "count");
        count.ConstraintExpression.Should().Be("floor(.) = . and (. > 0)");
        count.ConstraintMessage.Should().Be("Positive");
        package.Fields.Single(static field => field.FieldId == "name").RelevanceExpression.Should().Be("${count} > 1");
        var total = package.Fields.Single(static field => field.FieldId == "total");
        total.Type.Should().Be("integer");
        total.ReadOnly.Should().BeTrue();
        total.CalculatedExpression.Should().Be("${count} * 2");
    }

    [UnitTest]
    public void ToPackage_RuntimeQuestions_KeepTheirSubmissionBindings()
    {
        var package = Import(
            Row(2, "select_multiple status", "status"),
            Row(3, "geopoint", "where"),
            Row(4, "image", "photo"),
            Row(5, "begin_repeat", "rounds", "label", "Rounds"),
            Row(6, "text", "note"),
            Row(7, "end_repeat"));

        package.ExpressionLanguage.Should().Be("xlsform");
        package.SubmitPolicy.AllowAttachments.Should().BeTrue();
        package.Fields.Select(static field => (field.FieldId, field.Type, field.TargetField)).Should().Equal(
            ("status", "multiChoice", "status"), ("where", "location", null), ("photo", "media", null), ("note", "text", null));
        package.Sections.Single(static section => section.SectionId == "rounds").Repeatable.Should().BeTrue();
    }

    [UnitTest]
    public void ToPackage_UnrepresentableQuestions_ReportTheirSurveyRows()
    {
        var workbook = Workbook(
            Row(2, "select_multiple status", "status"),
            Row(3, "geopoint", "where"),
            Row(4, "calculate", "score", "calculation", "1"),
            Row(5, "begin_repeat", "visits", "label", "Visits"),
            Row(6, "text", "note"),
            Row(7, "end_repeat"));

        var result = FormXlsFormImportMapper.ToPackage(Convert(workbook), workbook, Target);

        result.Value.Should().BeNull();
        result.Diagnostics.Select(static diagnostic => (diagnostic.Sheet, diagnostic.RowNumber, diagnostic.Column)).Should().Equal(
            ("survey", 4, "calculation"), ("survey", 5, "type"));
    }

    [UnitTest]
    public void ToDefinition_ImportedPackage_RoundTripsThroughTheWorkbook()
    {
        var package = Import(
            Row(2, "begin_group", "site", "label", "Site", "hint", "Where"),
            Row(3, "text", "name", "label", "Name", "hint", "Full name", "required", "yes"),
            Row(4, "select_one status", "status", "label", "Status"),
            Row(5, "integer", "count", "label", "Count"),
            Row(6, "end_group"));

        var exported = XlsFormConverter.Export(FormXlsFormExportMapper.ToDefinition(Published(package)).Value!);
        exported.Diagnostics.Should().BeEmpty();
        var reimported = FormXlsFormImportMapper.ToPackage(XlsFormConverter.Import(exported.Value!).Value!, exported.Value!, Target);

        reimported.Value!.Fields.Should().BeEquivalentTo(package.Fields, static options => options
            .Using<JsonElement>(static context => context.Subject.GetRawText().Should().Be(context.Expectation.GetRawText()))
            .WhenTypeIs<JsonElement>());
        reimported.Value.Sections.Should().BeEquivalentTo(package.Sections);
        reimported.Value.Title.Should().Be(package.Title);
    }

    [UnitTest]
    public void ToDefinition_UnrepresentableBehavior_ReportsTheFieldRow()
    {
        var package = new FormPackageDocument
        {
            Title = "Inspection",
            Sections = [new FormSectionDefinition { SectionId = "main", Label = "Main", FieldIds = ["name", "flag"] }],
            Fields =
            [
                new FormFieldDefinition { FieldId = "name", Label = "Name", Type = "text", TargetField = "full_name" },
                new FormFieldDefinition { FieldId = "flag", Label = "Flag", Type = "boolean", TargetField = "flag", Private = true }
            ]
        };

        var result = FormXlsFormExportMapper.ToDefinition(Published(package));

        result.Value.Should().BeNull();
        result.Diagnostics.Select(static diagnostic => (diagnostic.RowNumber, diagnostic.Column)).Should().Equal(
            (3, "name"), (4, "type"), (4, "type"));
    }

    [UnitTest]
    public void ToDefinition_FieldsOutsideSections_ExportInAnUngroupedSection()
    {
        var package = new FormPackageDocument
        {
            Title = "Inspection",
            Fields = [new FormFieldDefinition { FieldId = "name", Label = "Name", Type = "text", TargetField = "name" }]
        };

        var definition = FormXlsFormExportMapper.ToDefinition(Published(package)).Value!;

        definition.Version.Should().Be("3");
        definition.Sections.Should().ContainSingle().Which.Fields.Should().ContainSingle()
            .Which.Should().Match<FormField>(field => field.FieldId == "name" && field.Type == FormFieldType.Text);
    }

    private static FormPackageDocument Import(params XlsFormRow[] survey)
    {
        var workbook = Workbook(survey);
        var result = FormXlsFormImportMapper.ToPackage(Convert(workbook), workbook, Target);
        result.Diagnostics.Should().BeEmpty();
        return result.Value!;
    }

    private static FormDefinition Convert(XlsFormWorkbook workbook)
    {
        var imported = XlsFormConverter.Import(workbook);
        imported.Diagnostics.Should().BeEmpty();
        return imported.Value!;
    }

    private static FormPackageVersion Published(FormPackageDocument package) => new()
    {
        FormId = "inspection",
        Version = 3,
        Status = FormPackageStatus.Published,
        Package = package
    };

    private static XlsFormWorkbook Workbook(params XlsFormRow[] survey) => new()
    {
        Survey = survey,
        Choices = [Cells(2, ("list_name", "status"), ("name", "open"), ("label", "Open")),
            Cells(3, ("list_name", "status"), ("name", "closed"), ("label", "Closed"))],
        Settings = [Cells(2, ("form_id", "inspection"), ("form_title", "Inspection"))]
    };

    private static XlsFormRow Row(int rowNumber, string type, params string[] rest)
    {
        var cells = new List<(string, string)> { ("type", type) };
        if (rest.Length > 0)
        {
            cells.Add(("name", rest[0]));
        }

        cells.AddRange(rest.Skip(1).Chunk(2).Select(static pair => (pair[0], pair[1])));
        return Cells(rowNumber, cells.ToArray());
    }

    private static XlsFormRow Cells(int rowNumber, params (string Column, string Value)[] cells) => new()
    {
        RowNumber = rowNumber,
        Cells = cells.ToDictionary(static cell => cell.Column, static cell => cell.Value, StringComparer.Ordinal)
    };
}
