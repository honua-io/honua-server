// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FieldWorkflows.Export;
using Honua.Core.Features.FieldWorkflows.Review;
using Honua.Core.Features.Forms.Packages;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.FieldWorkflows;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class FieldExportEndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("POST /api/v1/admin/field-workflows/exports")]
    [Endpoint("GET /api/v1/admin/field-workflows/exports")]
    public async Task CreateExport_GeoJson_ReturnsPackageRecordsAuditsAndListsHistory()
    {
        var submissionId = await SeedSubmissionAsync("export-geojson", "Inspection export A");

        var response = await _fixture.Client.PostAsync(
            "/api/v1/admin/field-workflows/exports",
            JsonContent(new FieldExportRequest { Format = FieldExportFormat.GeoJson },
                FieldExportJsonContext.Default.FieldExportRequest));

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/geo+json");
        response.Headers.GetValues("X-Honua-Export-Id").Should().ContainSingle();

        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("type").GetString().Should().Be("FeatureCollection");
        var features = document.RootElement.GetProperty("features");
        features.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);

        var exported = false;
        foreach (var feature in features.EnumerateArray())
        {
            var props = feature.GetProperty("properties");
            if (props.GetProperty("submissionId").GetString() == submissionId.ToString())
            {
                props.GetProperty("syncStatus").GetString().Should().Be(FieldSyncStatus.Synced);
                props.GetProperty("reviewStatus").GetString().Should().Be(FieldReviewStatus.Pending);
                feature.GetProperty("geometry").ValueKind.Should().Be(JsonValueKind.Null);
                exported = true;
            }
        }

        exported.Should().BeTrue("the seeded submission must appear in the export package");

        // The export is recorded for the audit trail.
        var auditCount = await CountAuditAsync("field.export.create");
        auditCount.Should().BeGreaterThanOrEqualTo(1);

        // Export history lists the generated package.
        var history = await GetJsonAsync(
            "/api/v1/admin/field-workflows/exports",
            FieldExportJsonContext.Default.FieldExportRecordListResult);
        history.Total.Should().BeGreaterThanOrEqualTo(1);
        history.Items.Should().Contain(item =>
            item.Format == FieldExportFormat.GeoJson && item.RecordCount >= 1);
    }

    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("POST /api/v1/admin/field-workflows/exports")]
    public async Task CreateExport_Csv_ReturnsCsvWithHeaderAndFilteredRows()
    {
        var included = await SeedSubmissionAsync("export-csv-included", "Included record");
        _ = await SeedSubmissionAsync("export-csv-other-service", "Other record");

        // Reject the "included" record so we can filter by review status.
        var decision = await _fixture.Client.PostAsync(
            $"/api/v1/admin/field-workflows/submissions/{included}/decision",
            JsonContent(new FieldReviewDecisionRequest { Status = FieldReviewStatus.Rejected },
                FieldReviewJsonContext.Default.FieldReviewDecisionRequest));
        decision.StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await _fixture.Client.PostAsync(
            "/api/v1/admin/field-workflows/exports",
            JsonContent(new FieldExportRequest
            {
                Format = FieldExportFormat.Csv,
                ReviewStatus = FieldReviewStatus.Rejected
            },
            FieldExportJsonContext.Default.FieldExportRequest));

        var csv = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, csv);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");

        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines[0].Should().StartWith("submissionId,formId,formVersion");
        csv.Should().Contain(included.ToString());
    }

    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("POST /api/v1/admin/field-workflows/exports")]
    public async Task CreateExport_InvalidFormat_Returns400()
    {
        var response = await _fixture.Client.PostAsync(
            "/api/v1/admin/field-workflows/exports",
            JsonContent(new FieldExportRequest { Format = "pdf" },
                FieldExportJsonContext.Default.FieldExportRequest));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private async Task<Guid> SeedSubmissionAsync(string suffix, string name)
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IFormPackageStore>();

        var draft = await store.SaveDraftAsync(CreatePackage("Field export " + suffix), "test-admin");
        var published = await store.PublishAsync(
            draft.FormId,
            draft.Version,
            new FormPackageValidationResult { IsValid = true },
            "test-admin")
            ?? throw new InvalidOperationException("Failed to publish seeded form package.");

        var submission = new FormSubmissionRequest
        {
            IdempotencyKey = suffix,
            Operation = FormSubmissionOperations.Create,
            ClientId = "field-client-1",
            Values = new Dictionary<string, JsonElement>
            {
                ["name"] = JsonDocument.Parse(JsonSerializer.Serialize(name)).RootElement.Clone()
            }
        };

        var submissionId = Guid.NewGuid();
        var claimed = await store.CreateSubmissionAsync(
            submissionId,
            suffix,
            "actor-hash-" + suffix,
            "request-hash-" + suffix,
            published,
            submission,
            "pending");
        claimed.Should().BeTrue();

        await store.CompleteSubmissionAsync(
            submissionId,
            new FormSubmissionResponse
            {
                SubmissionId = submissionId,
                Status = "accepted",
                FormId = published.FormId,
                FormVersion = published.Version,
                Operation = FormSubmissionOperations.Create,
                TargetFeatureId = 1
            },
            "accepted");

        return submissionId;
    }

    private async Task<long> CountAuditAsync(string action)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM honua.audit_log
            WHERE resource_type = 'field_export'
              AND action = @action;
            """;
        command.Parameters.AddWithValue("action", action);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private async Task<T> GetJsonAsync<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> jsonTypeInfo)
    {
        var response = await _fixture.Client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadAsStringAsync();
        var value = JsonSerializer.Deserialize(payload, jsonTypeInfo);
        value.Should().NotBeNull(payload);
        return value!;
    }

    private static StringContent JsonContent<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> jsonTypeInfo)
        => new(JsonSerializer.Serialize(value, jsonTypeInfo), Encoding.UTF8, "application/json");

    private static FormPackageDocument CreatePackage(string title)
        => new()
        {
            Title = title,
            Target = new FormTargetDefinition
            {
                ServiceId = WebAppFixture.TestServiceId,
                LayerId = WebAppFixture.TestLayerId
            },
            Sections =
            [
                new FormSectionDefinition { SectionId = "main", Label = "Main", FieldIds = ["name"] }
            ],
            Fields =
            [
                new FormFieldDefinition
                {
                    FieldId = "name",
                    Label = "Name",
                    Type = "text",
                    TargetField = "name",
                    Required = true,
                    SectionId = "main"
                }
            ],
            SubmitPolicy = new FormSubmitPolicy
            {
                AllowedOperations = [FormSubmissionOperations.Create],
                RequiresGeometry = true
            },
            OfflinePolicy = new FormOfflinePolicy { Enabled = true }
        };
}
