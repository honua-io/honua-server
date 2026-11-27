// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Forms.Packages;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Forms;

public sealed partial class FormPackageEndpointsTests
{
    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("POST /api/v1/admin/forms/import")]
    [Endpoint("POST /api/v1/forms/{formId}/submissions")]
    [Endpoint("GET /api/v1/admin/forms/{formId}/export")]
    public async Task XlsFormImport_PublishSubmitAndExport_PreservesSupportedSurvey()
    {
        using var file = XlsFormUpload("text", options: new() { Constraint = "string-length(.) >= 3" });
        var draft = await ImportXlsFormAsync(file, "survey.xlsx");
        draft.Status.Should().Be(FormPackageStatus.Draft);
        draft.Package.Target!.ServiceId.Should().Be(WebAppFixture.TestServiceId);
        draft.Package.Target.LayerId.Should().Be(WebAppFixture.TestLayerId);
        draft.Package.ExpressionLanguage.Should().Be("xlsform");
        draft.Package.Fields.Should().ContainSingle(field => field.FieldId == "name" && field.Required &&
            field.ConstraintExpression == "string-length(.) >= 3");

        var validation = await PostJsonAsync($"/api/v1/admin/forms/{draft.FormId}/versions/1/validate",
            FormPackageJsonContext.Default.FormPackageValidationResult);
        validation.IsValid.Should().BeTrue();
        var published = await PostJsonAsync($"/api/v1/admin/forms/{draft.FormId}/versions/1/publish",
            FormPackageJsonContext.Default.FormPackageVersion);
        published.Status.Should().Be(FormPackageStatus.Published);

        await AssertXlsFormSubmissionAsync(draft.FormId);

        using var reimportFile = new ByteArrayContent(await ExportXlsFormAsync(draft.FormId));
        var roundTrip = await ImportXlsFormAsync(reimportFile, "exported.xlsx");
        roundTrip.FormId.Should().Be(draft.FormId);
        roundTrip.Package.Title.Should().Be(draft.Package.Title);
        roundTrip.Package.Target.Should().BeEquivalentTo(draft.Package.Target);
        roundTrip.Package.Fields.Should().BeEquivalentTo(draft.Package.Fields);
    }

    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("POST /api/v1/admin/forms/import")]
    [Endpoint("GET /api/v1/forms/{formId}")]
    public async Task XlsFormImport_PublishExposesWorkbookLiteralDefault()
    {
        using var file = XlsFormUpload("text", options: new() { DefaultValue = "North workshop" });
        var draft = await ImportXlsFormAsync(file, "survey.xlsx");
        var published = await PostJsonAsync($"/api/v1/admin/forms/{draft.FormId}/versions/1/publish",
            FormPackageJsonContext.Default.FormPackageVersion);
        published.Status.Should().Be(FormPackageStatus.Published);

        var runtime = await GetJsonAsync($"/api/v1/forms/{published.FormId}", FormPackageJsonContext.Default.FormPackageVersion);
        runtime.Package.Fields.Should().ContainSingle().Which.DefaultValue!.Value.GetString().Should().Be("North workshop");
    }

    private async Task AssertXlsFormSubmissionAsync(string formId)
    {
        using var invalidContent = JsonContent(CreateSubmission($"xlsform-{Guid.NewGuid():N}", "ab"));
        using var rejected = await _fixture.Client.PostAsync(
            $"/api/v1/forms/{formId}/submissions",
            invalidContent);
        var rejection = await ReadJsonAsync(rejected, FormPackageJsonContext.Default.FormSubmissionResponse);
        rejection.Status.Should().Be("rejected");
        rejection.ValidationIssues.Should().Contain(issue => issue.FieldId == "name");

        using var submissionContent = JsonContent(CreateSubmission($"xlsform-{Guid.NewGuid():N}", "Imported field feature"));
        using var submitted = await _fixture.Client.PostAsync(
            $"/api/v1/forms/{formId}/submissions",
            submissionContent);
        submitted.StatusCode.Should().Be(HttpStatusCode.OK);
        var submission = await ReadJsonAsync(submitted, FormPackageJsonContext.Default.FormSubmissionResponse);
        submission.Status.Should().Be("accepted");
        submission.EditOutcome!.Created.Should().Be(1);
        submission.TargetFeatureId.Should().NotBeNull();
    }

    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("POST /api/v1/admin/forms/import")]
    public async Task XlsFormImport_UnsupportedSurveyType_ReportsCellAndCreatesNoDraft()
    {
        using var file = XlsFormUpload("unsupported_widget");
        using var content = new MultipartFormDataContent();
        content.Add(file, "file", "survey.xlsx");
        using var response = await _fixture.Client.PostAsync(XlsFormImportUrl(), content);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("survey").And.Contain("type").And.Contain("diagnostics");
        var packages = await GetJsonAsync("/api/v1/admin/forms", FormPackageJsonContext.Default.FormPackageSummaryArray);
        packages.Should().NotContain(package => package.FormId == _xlsFormId);
    }

    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("POST /api/v1/admin/forms/import")]
    public async Task XlsFormImport_InvalidArchive_ReturnsBadRequest()
    {
        using var content = new MultipartFormDataContent();
        using var file = new ByteArrayContent([1, 2, 3]);
        content.Add(file, "file", "invalid.xlsx");
        using var response = await _fixture.Client.PostAsync(XlsFormImportUrl(), content);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("POST /api/v1/admin/forms/import")]
    public async Task XlsFormImport_OversizedWorkbook_ReturnsPayloadTooLarge()
    {
        using var content = new MultipartFormDataContent();
        using var file = new ByteArrayContent(new byte[(9 * 1024 * 1024)]);
        content.Add(file, "file", "oversized.xlsx");
        using var response = await _fixture.Client.PostAsync(XlsFormImportUrl(), content);
        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [IntegrationTheory]
    [InlineData("import")]
    [InlineData("survey.v2")]
    [Operation(Operations.Configuration)]
    [Endpoint("POST /api/v1/admin/forms/import")]
    public async Task XlsFormImport_UnsafeFormId_ReportsSettingsCellAndCreatesNoDraft(string formId)
    {
        using var file = XlsFormUpload("text", formId);
        using var content = new MultipartFormDataContent();
        content.Add(file, "file", "survey.xlsx");
        using var response = await _fixture.Client.PostAsync(XlsFormImportUrl(), content);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("settings").And.Contain("form_id");
        var packages = await GetJsonAsync("/api/v1/admin/forms", FormPackageJsonContext.Default.FormPackageSummaryArray);
        packages.Should().NotContain(package => package.FormId == formId);
    }

    private readonly string _xlsFormId = $"survey_{Guid.NewGuid():N}";

    private async Task<FormPackageVersion> ImportXlsFormAsync(HttpContent file, string fileName)
    {
        using var content = new MultipartFormDataContent();
        content.Add(file, "file", fileName);
        using var imported = await _fixture.Client.PostAsync(XlsFormImportUrl(), content);
        imported.StatusCode.Should().Be(HttpStatusCode.Created, await imported.Content.ReadAsStringAsync());
        return await ReadJsonAsync(imported, FormPackageJsonContext.Default.FormPackageVersion);
    }

    private async Task<byte[]> ExportXlsFormAsync(string formId)
    {
        using var exported = await _fixture.Client.GetAsync($"/api/v1/admin/forms/{formId}/export");
        exported.StatusCode.Should().Be(HttpStatusCode.OK);
        exported.Content.Headers.ContentType!.MediaType.Should().Be("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        var workbook = await exported.Content.ReadAsByteArrayAsync();
        using var archive = new ZipArchive(new MemoryStream(workbook));
        archive.GetEntry("xl/workbook.xml").Should().NotBeNull();
        return workbook;
    }

    private static string XlsFormImportUrl()
        => $"/api/v1/admin/forms/import?serviceId={Uri.EscapeDataString(WebAppFixture.TestServiceId)}&layerId={WebAppFixture.TestLayerId}";

    private ByteArrayContent XlsFormUpload(string fieldType, string? formId = null, XlsFormUploadOptions? options = null)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteXlsFormPart(archive, "xl/workbook.xml", """
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="survey" sheetId="1" r:id="s1"/><sheet name="settings" sheetId="2" r:id="s2"/></sheets></workbook>
                """);
            WriteXlsFormPart(archive, "xl/_rels/workbook.xml.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="s1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/survey.xml"/><Relationship Id="s2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/settings.xml"/></Relationships>
                """);
            WriteXlsFormPart(archive, "xl/worksheets/survey.xml", XlsFormSheet(
                ["type", "name", "label", "required", "constraint", "default"], [fieldType, "name", "Name", "yes", options?.Constraint ?? "", options?.DefaultValue ?? ""]));
            WriteXlsFormPart(archive, "xl/worksheets/settings.xml", XlsFormSheet(
                ["form_id", "form_title"], [formId ?? _xlsFormId, "Workbook inspection"]));
        }

        return new ByteArrayContent(buffer.ToArray())
        {
            Headers = { ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet") }
        };
    }

    private sealed class XlsFormUploadOptions
    {
        public string Constraint { get; init; } = "";
        public string DefaultValue { get; init; } = "";
    }

    private static string XlsFormSheet(params string[][] rows)
    {
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        return new XElement(ns + "worksheet", new XElement(ns + "sheetData", rows.Select((cells, row) =>
            new XElement(ns + "row", new XAttribute("r", row + 1), cells.Select((value, column) =>
                new XElement(ns + "c", new XAttribute("r", $"{(char)('A' + column)}{row + 1}"),
                    new XAttribute("t", "inlineStr"), new XElement(ns + "is", new XElement(ns + "t", value)))))))).ToString();
    }

    private static void WriteXlsFormPart(ZipArchive archive, string path, string xml)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open(), Encoding.UTF8);
        writer.Write(xml);
    }
}
