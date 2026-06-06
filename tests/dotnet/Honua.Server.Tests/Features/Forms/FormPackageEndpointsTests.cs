// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Forms.Packages;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Honua.Server.Tests.Features.Forms;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class FormPackageEndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        await EnableEditingCapabilitiesAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("GET /api/v1/admin/forms/packages")]
    [Endpoint("POST /api/v1/admin/forms/packages")]
    [Endpoint("GET /api/v1/admin/forms/packages/{formId}")]
    [Endpoint("GET /api/v1/admin/forms/packages/{formId}/versions")]
    [Endpoint("GET /api/v1/admin/forms/packages/{formId}/versions/{packageVersion}")]
    [Endpoint("PUT /api/v1/admin/forms/packages/{formId}/versions/{packageVersion}")]
    [Endpoint("POST /api/v1/admin/forms/packages/{formId}/versions/{packageVersion}/validate")]
    [Endpoint("POST /api/v1/admin/forms/packages/{formId}/versions/{packageVersion}/publish")]
    [Endpoint("POST /api/v1/admin/forms/packages/{formId}/versions/{packageVersion}/reopen")]
    [Endpoint("GET /api/v1/forms/packages/{formId}")]
    [Endpoint("GET /api/v1/forms/packages/{formId}/versions/{packageVersion}")]
    [Endpoint("GET /api/v1/forms/packages/{formId}/offline-policy")]
    public async Task PackageLifecycle_DraftValidatePublishReadReopenAndUpdate_Works()
    {
        var draft = await CreateDraftAsync(CreatePackage("Lifecycle inspection"));
        draft.Status.Should().Be(FormPackageStatus.Draft);
        draft.Version.Should().Be(1);

        var list = await GetJsonAsync($"/api/v1/admin/forms/packages", FormPackageJsonContext.Default.FormPackageSummaryArray);
        list.Should().Contain(summary => summary.FormId == draft.FormId && summary.CurrentDraftVersion == 1);

        var currentDraft = await GetJsonAsync($"/api/v1/admin/forms/packages/{draft.FormId}", FormPackageJsonContext.Default.FormPackageVersion);
        currentDraft.Status.Should().Be(FormPackageStatus.Draft);

        var versions = await GetJsonAsync($"/api/v1/admin/forms/packages/{draft.FormId}/versions", FormPackageJsonContext.Default.FormPackageVersionArray);
        versions.Should().ContainSingle(version => version.Version == 1);

        var draftByVersion = await GetJsonAsync($"/api/v1/admin/forms/packages/{draft.FormId}/versions/1", FormPackageJsonContext.Default.FormPackageVersion);
        draftByVersion.ETag.Should().Be(draft.ETag);

        var validation = await PostJsonAsync(
            $"/api/v1/admin/forms/packages/{draft.FormId}/versions/1/validate",
            FormPackageJsonContext.Default.FormPackageValidationResult);
        validation.IsValid.Should().BeTrue();

        var published = await PostJsonAsync(
            $"/api/v1/admin/forms/packages/{draft.FormId}/versions/1/publish",
            FormPackageJsonContext.Default.FormPackageVersion);
        published.Status.Should().Be(FormPackageStatus.Published);
        published.PublishedAt.Should().NotBeNull();

        using (var updatePublished = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/v1/admin/forms/packages/{published.FormId}/versions/1")
        {
            Content = JsonContent(CreatePackage("Published update attempt"))
        })
        {
            updatePublished.Headers.TryAddWithoutValidation("If-Match", published.ETag);
            var conflict = await _fixture.Client.SendAsync(updatePublished);
            conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }

        var runtimeCurrent = await GetJsonAsync($"/api/v1/forms/packages/{published.FormId}", FormPackageJsonContext.Default.FormPackageVersion);
        runtimeCurrent.Status.Should().Be(FormPackageStatus.Published);

        var runtimeVersion = await GetJsonAsync($"/api/v1/forms/packages/{published.FormId}/versions/1", FormPackageJsonContext.Default.FormPackageVersion);
        runtimeVersion.Version.Should().Be(1);

        var offlinePolicy = await GetJsonAsync($"/api/v1/forms/packages/{published.FormId}/offline-policy", FormPackageJsonContext.Default.FormOfflinePolicyResponse);
        offlinePolicy.Enabled.Should().BeTrue();
        offlinePolicy.AvailableTransports.Should().Contain(["feature-server-replica", "fieldcollection"]);
        offlinePolicy.RequiredHeaders.Should().ContainKey("X-Honua-Client-Id");
        offlinePolicy.Links.Should().Contain(link => link.Rel == "create-replica");
        offlinePolicy.Links.Should().Contain(link => link.Rel == "fieldcollection-generation");

        var reopened = await PostJsonAsync(
            $"/api/v1/admin/forms/packages/{published.FormId}/versions/1/reopen",
            FormPackageJsonContext.Default.FormPackageVersion);
        reopened.Status.Should().Be(FormPackageStatus.Draft);
        reopened.Version.Should().Be(2);
        reopened.ReopenedFromVersion.Should().Be(1);

        using var updateDraft = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/v1/admin/forms/packages/{published.FormId}/versions/2")
        {
            Content = JsonContent(CreatePackage("Lifecycle inspection updated"))
        };
        updateDraft.Headers.TryAddWithoutValidation("If-Match", reopened.ETag);
        var updateResponse = await _fixture.Client.SendAsync(updateDraft);
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await ReadJsonAsync(updateResponse, FormPackageJsonContext.Default.FormPackageVersion);
        updated.Package.Title.Should().Be("Lifecycle inspection updated");
    }

    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("POST /api/v1/admin/forms/packages")]
    [Endpoint("POST /api/v1/admin/forms/packages/{formId}/versions/{packageVersion}/validate")]
    public async Task PackageValidation_WithMissingDomainChoiceCode_ReturnsValidationIssueWithoutDraftSaveFailure()
    {
        var packageJson = $$"""
            {
              "schemaVersion": "honua.form-package.v1",
              "title": "Missing choice code",
              "target": { "serviceId": "{{WebAppFixture.TestServiceId}}", "layerId": {{WebAppFixture.TestLayerId}} },
              "sections": [ { "sectionId": "main", "label": "Main", "fieldIds": ["category"] } ],
              "fields": [
                {
                  "fieldId": "category",
                  "label": "Category",
                  "type": "choice",
                  "targetField": "category",
                  "sectionId": "main",
                  "domain": { "type": "codedValue", "choices": [ { "label": "Missing code" } ] }
                }
              ],
              "submitPolicy": { "allowedOperations": ["create"] }
            }
            """;

        var createResponse = await _fixture.Client.PostAsync("/api/v1/admin/forms/packages", JsonContent(packageJson));
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var draft = await ReadJsonAsync(createResponse, FormPackageJsonContext.Default.FormPackageVersion);

        var validation = await PostJsonAsync(
            $"/api/v1/admin/forms/packages/{draft.FormId}/versions/{draft.Version}/validate",
            FormPackageJsonContext.Default.FormPackageValidationResult);

        validation.IsValid.Should().BeFalse();
        validation.Issues.Should().Contain(issue => issue.Code == "domainChoiceCodeRequired");
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/forms/packages/{formId}/submissions")]
    public async Task SubmitForm_WithIdempotency_ReplaysSamePayloadAndRejectsChangedPayload()
    {
        var published = await PublishPackageAsync(CreatePackage("Idempotent create"));
        var payload = JsonSerializer.Serialize(
            CreateSubmission("idem-create-1", "Idempotent feature"),
            FormPackageJsonContext.Default.FormSubmissionRequest);

        var first = await _fixture.Client.PostAsync(
            $"/api/v1/forms/packages/{published.FormId}/submissions",
            JsonContent(payload));
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstBody = await ReadJsonAsync(first, FormPackageJsonContext.Default.FormSubmissionResponse);
        firstBody.Status.Should().Be("accepted");
        firstBody.EditOutcome.Should().NotBeNull();
        firstBody.EditOutcome!.Created.Should().Be(1);
        firstBody.TargetFeatureId.Should().NotBeNull();

        var replay = await _fixture.Client.PostAsync(
            $"/api/v1/forms/packages/{published.FormId}/submissions",
            JsonContent(payload));
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        var replayBody = await ReadJsonAsync(replay, FormPackageJsonContext.Default.FormSubmissionResponse);
        replayBody.IdempotentReplay.Should().BeTrue();
        replayBody.SubmissionId.Should().Be(firstBody.SubmissionId);

        var changedPayload = JsonSerializer.Serialize(
            CreateSubmission("idem-create-1", "Changed feature"),
            FormPackageJsonContext.Default.FormSubmissionRequest);
        var changed = await _fixture.Client.PostAsync(
            $"/api/v1/forms/packages/{published.FormId}/submissions",
            JsonContent(changedPayload));
        changed.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task StoreSubmissionClaim_WithDuplicateIdempotencyKey_ReportsOnlyFirstOwner()
    {
        var published = await PublishPackageAsync(CreatePackage("Store idempotency claim"));
        var submission = CreateSubmission("store-claim-1", "Store claim");

        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IFormPackageStore>();

        var submissionId = Guid.NewGuid();
        var firstClaimed = await store.CreateSubmissionAsync(
            submissionId,
            submission.IdempotencyKey,
            "actor-hash",
            "request-hash",
            published,
            submission,
            "pending");
        var secondClaimed = await store.CreateSubmissionAsync(
            Guid.NewGuid(),
            submission.IdempotencyKey,
            "actor-hash",
            "request-hash",
            published,
            submission,
            "pending");

        firstClaimed.Should().BeTrue();
        secondClaimed.Should().BeFalse();

        var accepted = new FormSubmissionResponse
        {
            SubmissionId = submissionId,
            Status = "accepted",
            FormId = published.FormId,
            FormVersion = published.Version,
            Operation = FormSubmissionOperations.Create,
            TargetFeatureId = 42
        };
        var failed = new FormSubmissionResponse
        {
            SubmissionId = submissionId,
            Status = "failed",
            FormId = published.FormId,
            FormVersion = published.Version,
            Operation = FormSubmissionOperations.Create
        };
        await store.CompleteSubmissionAsync(submissionId, accepted, "accepted");
        await store.CompleteSubmissionAsync(submissionId, failed, "failed");

        var persisted = await store.GetSubmissionByIdempotencyAsync(
            published.FormId,
            published.Version,
            "actor-hash",
            submission.IdempotencyKey!);
        persisted.Should().NotBeNull();
        persisted!.Status.Should().Be("accepted");
        persisted.Response.Should().NotBeNull();
        persisted.Response!.Status.Should().Be("accepted");
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /api/v1/forms/packages/{formId}/submissions")]
    public async Task SubmitForm_WithOperationDeniedByPackage_ReturnsRejectedPolicyResponse()
    {
        var published = await PublishPackageAsync(CreatePackage("Create only"));
        var update = CreateSubmission(
            "denied-update-1",
            "Denied update",
            operation: FormSubmissionOperations.Update,
            targetFeatureId: 1);

        var response = await _fixture.Client.PostAsync(
            $"/api/v1/forms/packages/{published.FormId}/submissions",
            JsonContent(update));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await ReadJsonAsync(response, FormPackageJsonContext.Default.FormSubmissionResponse);
        body.Status.Should().Be("rejected");
        body.ValidationIssues.Should().Contain(issue => issue.Code == "operationNotAllowed");
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/forms/packages/{formId}/submissions")]
    public async Task SubmitForm_WithNonnumericGeometry_ReturnsRejectedValidationResponse()
    {
        var published = await PublishPackageAsync(CreatePackage("Geometry validation"));
        var submission = CreateSubmission(
            "bad-geometry-1",
            "Bad geometry",
            geometry: Json("""{"x":"west","y":21.3069,"spatialReference":{"wkid":4326}}"""));

        var response = await _fixture.Client.PostAsync(
            $"/api/v1/forms/packages/{published.FormId}/submissions",
            JsonContent(submission));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await ReadJsonAsync(response, FormPackageJsonContext.Default.FormSubmissionResponse);
        body.Status.Should().Be("rejected");
        body.ValidationIssues.Should().Contain(issue => issue.Code == "geometryCoordinateInvalid");
    }

    [IntegrationTest]
    [Operation(Operations.AddAttachment)]
    [Endpoint("POST /api/v1/forms/packages/{formId}/submissions")]
    public async Task SubmitForm_WithMultipartAttachment_PersistsFeatureAttachmentAndMinimizedSubmissionRecord()
    {
        var published = await PublishPackageAsync(CreatePackage("Attachment create", includeAttachment: true));
        var submission = CreateSubmission("attachment-create-1", "Private attachment value", includeAttachment: true);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(
            JsonSerializer.Serialize(submission, FormPackageJsonContext.Default.FormSubmissionRequest),
            Encoding.UTF8,
            "application/json"), "submission");

        var file = new ByteArrayContent(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII="));
        file.Headers.ContentType = MediaTypeHeaderValue.Parse("image/png");
        form.Add(file, "photo-file", "photo.png");

        var response = await _fixture.Client.PostAsync(
            $"/api/v1/forms/packages/{published.FormId}/submissions",
            form);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadJsonAsync(response, FormPackageJsonContext.Default.FormSubmissionResponse);
        body.Status.Should().Be("accepted");
        body.AttachmentOutcomes.Should().ContainSingle(outcome =>
            outcome.Status == "accepted" &&
            outcome.FieldId == "photo" &&
            outcome.AttachmentId.HasValue &&
            outcome.PrivacyApplied);

        var (attachmentCount, requestSummary) = await ReadSubmissionPersistenceAsync(body.SubmissionId);
        attachmentCount.Should().Be(1);
        using var summaryDocument = JsonDocument.Parse(requestSummary);
        var privateFieldIds = summaryDocument.RootElement.GetProperty("privateFieldIds").EnumerateArray()
            .Select(static value => value.GetString())
            .ToArray();
        privateFieldIds.Should().Equal("name");
        requestSummary.Should().NotContain("Private attachment value");
    }

    [IntegrationTest]
    [Operation(Operations.AddAttachment)]
    [Endpoint("POST /api/v1/forms/packages/{formId}/submissions")]
    public async Task SubmitForm_WithMultipartAttachmentRejectedByFieldPolicy_ReturnsRejectedResponse()
    {
        var published = await PublishPackageAsync(CreatePackage(
            "Attachment field policy",
            includeAttachment: true,
            attachmentAllowedContentTypes: ["image/*"],
            fieldAttachmentAllowedContentTypes: ["image/png"]));
        var submission = CreateSubmission("attachment-field-policy-1", "Field policy", includeAttachment: true);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(
            JsonSerializer.Serialize(submission, FormPackageJsonContext.Default.FormSubmissionRequest),
            Encoding.UTF8,
            "application/json"), "submission");

        var file = new ByteArrayContent(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII="));
        file.Headers.ContentType = MediaTypeHeaderValue.Parse("image/jpeg");
        form.Add(file, "photo-file", "photo.jpg");

        var response = await _fixture.Client.PostAsync(
            $"/api/v1/forms/packages/{published.FormId}/submissions",
            form);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await ReadJsonAsync(response, FormPackageJsonContext.Default.FormSubmissionResponse);
        body.Status.Should().Be("rejected");
        body.ValidationIssues.Should().Contain(issue =>
            issue.Code == "attachmentContentTypeNotAllowed" &&
            issue.FieldId == "photo");
        body.AttachmentOutcomes.Should().ContainSingle(outcome =>
            outcome.Status == "rejected" &&
            outcome.FieldId == "photo");
        var rejectedAttachmentCount = await CountAttachmentOutcomesAsync(body.SubmissionId, "rejected");
        rejectedAttachmentCount.Should().Be(1);
    }

    private async Task EnableEditingCapabilitiesAsync()
    {
        await _fixture.Postgres.ExecuteAsync("""
            UPDATE honua.services
            SET capabilities = ARRAY['Query', 'Extract', 'Create', 'Update', 'Delete']
            WHERE service_name = 'test';
            """, _fixture.CurrentSchema);
    }

    private async Task<FormPackageVersion> CreateDraftAsync(FormPackageDocument package)
    {
        var response = await _fixture.Client.PostAsync("/api/v1/admin/forms/packages", JsonContent(package));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return await ReadJsonAsync(response, FormPackageJsonContext.Default.FormPackageVersion);
    }

    private async Task<FormPackageVersion> PublishPackageAsync(FormPackageDocument package)
    {
        var draft = await CreateDraftAsync(package);
        var validation = await PostJsonAsync(
            $"/api/v1/admin/forms/packages/{draft.FormId}/versions/{draft.Version}/validate",
            FormPackageJsonContext.Default.FormPackageValidationResult);
        validation.IsValid.Should().BeTrue(validation.Issues.Length == 0 ? string.Empty : validation.Issues[0].Message);

        return await PostJsonAsync(
            $"/api/v1/admin/forms/packages/{draft.FormId}/versions/{draft.Version}/publish",
            FormPackageJsonContext.Default.FormPackageVersion);
    }

    private async Task<(long AttachmentCount, string RequestSummary)> ReadSubmissionPersistenceAsync(Guid submissionId)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM honua.form_submission_attachments WHERE submission_id = @submission_id AND status = 'accepted') AS attachment_count,
                request_summary::text
            FROM honua.form_submissions
            WHERE submission_id = @submission_id;
            """;
        command.Parameters.AddWithValue("submission_id", submissionId);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return (reader.GetInt64(0), reader.GetString(1));
    }

    private async Task<long> CountAttachmentOutcomesAsync(Guid submissionId, string status)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM honua.form_submission_attachments
            WHERE submission_id = @submission_id AND status = @status;
            """;
        command.Parameters.AddWithValue("submission_id", submissionId);
        command.Parameters.AddWithValue("status", status);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("POST /api/v1/admin/forms/packages/generate")]
    public async Task GenerateFormPackage_MissingPrompt_ReachesHandlerAndReturnsBadRequest()
    {
        // The generate route validates the prompt before invoking any AI provider, so an empty body
        // exercises the wired endpoint (non-404) without calling a real LLM.
        var response = await _fixture.Client.PostAsync(
            "/api/v1/admin/forms/packages/generate",
            JsonContent("{}"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private async Task<T> GetJsonAsync<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> jsonTypeInfo)
    {
        var response = await _fixture.Client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await ReadJsonAsync(response, jsonTypeInfo);
    }

    private async Task<T> PostJsonAsync<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> jsonTypeInfo)
    {
        var response = await _fixture.Client.PostAsync(path, null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await ReadJsonAsync(response, jsonTypeInfo);
    }

    private static async Task<T> ReadJsonAsync<T>(
        HttpResponseMessage response,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> jsonTypeInfo)
    {
        var payload = await response.Content.ReadAsStringAsync();
        var value = JsonSerializer.Deserialize(payload, jsonTypeInfo);
        value.Should().NotBeNull(payload);
        return value!;
    }

    private static StringContent JsonContent(FormPackageDocument package)
        => JsonContent(JsonSerializer.Serialize(package, FormPackageJsonContext.Default.FormPackageDocument));

    private static StringContent JsonContent(FormSubmissionRequest submission)
        => JsonContent(JsonSerializer.Serialize(submission, FormPackageJsonContext.Default.FormSubmissionRequest));

    private static StringContent JsonContent(string json)
        => new(json, Encoding.UTF8, "application/json");

    private static FormPackageDocument CreatePackage(
        string title,
        bool includeAttachment = false,
        string[]? attachmentAllowedContentTypes = null,
        string[]? fieldAttachmentAllowedContentTypes = null)
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
                new FormSectionDefinition
                {
                    SectionId = "main",
                    Label = "Main",
                    FieldIds = includeAttachment ? ["name", "photo"] : ["name"]
                }
            ],
            Fields = includeAttachment
                ?
                [
                    CreateNameField(privateField: true),
                    new FormFieldDefinition
                    {
                        FieldId = "photo",
                        Label = "Photo",
                        Type = "attachment",
                        Required = true,
                        SectionId = "main"
                    }
                ]
                : [CreateNameField(privateField: false)],
            SubmitPolicy = new FormSubmitPolicy
            {
                AllowedOperations = [FormSubmissionOperations.Create],
                RequiresGeometry = true,
                AllowAttachments = includeAttachment
            },
            AttachmentPolicy = includeAttachment
                ? new FormAttachmentPolicy
                {
                    Enabled = true,
                    MaxAttachmentsPerSubmission = 2,
                    MaxAttachmentBytes = 1_000_000,
                    MaxTotalBytes = 2_000_000,
                    AllowedContentTypes = attachmentAllowedContentTypes ?? ["image/png"],
                    Fields =
                    [
                        new FormFieldAttachmentPolicy
                        {
                            FieldId = "photo",
                            MaxCount = 1,
                            Required = true,
                            AllowedContentTypes = fieldAttachmentAllowedContentTypes ?? ["image/png"]
                        }
                    ]
                }
                : new FormAttachmentPolicy(),
            PrivacyPolicy = includeAttachment
                ? new FormPrivacyPolicy
                {
                    PrivateFieldIds = ["name"],
                    RequiredTransformations = ["minimizeAudit"],
                    CaptureActor = true,
                    CaptureDeviceId = true,
                    RetentionDays = 30
                }
                : new FormPrivacyPolicy(),
            OfflinePolicy = new FormOfflinePolicy
            {
                Enabled = true,
                PreferredTransports = ["feature-server-replica", "fieldcollection"],
                ReplicaTransportEnabled = true,
                FieldCollectionTransportEnabled = true
            }
        };

    private static FormFieldDefinition CreateNameField(bool privateField)
        => new()
        {
            FieldId = "name",
            Label = "Name",
            Type = "text",
            TargetField = "name",
            Required = true,
            SectionId = "main",
            Private = privateField
        };

    private static FormSubmissionRequest CreateSubmission(
        string idempotencyKey,
        string name,
        string operation = FormSubmissionOperations.Create,
        bool includeAttachment = false,
        long? targetFeatureId = null,
        JsonElement? geometry = null,
        string attachmentFilename = "photo.png",
        string attachmentContentType = "image/png")
        => new()
        {
            IdempotencyKey = idempotencyKey,
            Operation = operation,
            TargetFeatureId = targetFeatureId,
            ClientId = "field-client-1",
            Values = new Dictionary<string, JsonElement>
            {
                ["name"] = Json(JsonSerializer.Serialize(name))
            },
            Geometry = geometry ?? Json("""{"x":-157.8583,"y":21.3069,"spatialReference":{"wkid":4326}}"""),
            Attachments = includeAttachment
                ?
                [
                    new FormSubmissionAttachmentDescriptor
                    {
                        ClientAttachmentId = "photo-1",
                        FieldId = "photo",
                        PartName = "photo-file",
                        Filename = attachmentFilename,
                        ContentType = attachmentContentType
                    }
                ]
                : []
        };

    private static JsonElement Json(string json)
        => JsonDocument.Parse(json).RootElement.Clone();
}
