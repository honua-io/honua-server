// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FieldWorkflows.Review;
using Honua.Core.Features.Forms.Packages;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.FieldWorkflows;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class FieldReviewEndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("GET /api/v1/admin/field-workflows/submissions")]
    [Endpoint("GET /api/v1/admin/field-workflows/submissions/{submissionId}")]
    [Endpoint("POST /api/v1/admin/field-workflows/submissions/{submissionId}/assignment")]
    [Endpoint("POST /api/v1/admin/field-workflows/submissions/{submissionId}/decision")]
    [Endpoint("POST /api/v1/admin/field-workflows/submissions/{submissionId}/comments")]
    public async Task ReviewWorkflow_ListAssignCommentApprove_TransitionsAndAudits()
    {
        var submissionId = await SeedSubmissionAsync("review-flow-1", "Inspection A");

        // List surfaces the submitted record with default pending review state and
        // mobile-consumable metadata (sync status, device id, submitter hash).
        var list = await GetJsonAsync(
            "/api/v1/admin/field-workflows/submissions",
            FieldReviewJsonContext.Default.FieldSubmissionListResult);
        list.Total.Should().BeGreaterThanOrEqualTo(1);
        var listed = list.Items.Should().ContainSingle(item => item.SubmissionId == submissionId).Subject;
        listed.Review.Status.Should().Be(FieldReviewStatus.Pending);
        listed.SyncStatus.Should().Be(FieldSyncStatus.Synced);
        listed.DeviceId.Should().Be("field-client-1");
        listed.SubmitterHash.Should().NotBeNullOrEmpty();

        // Detail returns review state, comments, and attachment metadata.
        var detail = await GetJsonAsync(
            $"/api/v1/admin/field-workflows/submissions/{submissionId}",
            FieldReviewJsonContext.Default.FieldSubmissionDetail);
        detail.Record.SubmissionId.Should().Be(submissionId);
        detail.Comments.Should().BeEmpty();

        // Assignment moves a pending record to in_review.
        var assigned = await PostJsonAsync(
            $"/api/v1/admin/field-workflows/submissions/{submissionId}/assignment",
            new FieldReviewAssignmentRequest { AssignedTo = "reviewer-1" },
            FieldReviewJsonContext.Default.FieldReviewAssignmentRequest,
            FieldReviewJsonContext.Default.FieldReviewState);
        assigned.Status.Should().Be(FieldReviewStatus.InReview);
        assigned.AssignedTo.Should().Be("reviewer-1");

        // A correction-request comment transitions to changes_requested.
        var commentResponse = await _fixture.Client.PostAsync(
            $"/api/v1/admin/field-workflows/submissions/{submissionId}/comments",
            JsonContent(new FieldReviewCommentRequest { Body = "Please retake the photo.", CorrectionRequest = true },
                FieldReviewJsonContext.Default.FieldReviewCommentRequest));
        commentResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var afterComment = await GetJsonAsync(
            $"/api/v1/admin/field-workflows/submissions/{submissionId}",
            FieldReviewJsonContext.Default.FieldSubmissionDetail);
        afterComment.Record.Review.Status.Should().Be(FieldReviewStatus.ChangesRequested);
        afterComment.Comments.Should().ContainSingle(c => c.CorrectionRequest && c.Body == "Please retake the photo.");

        // Approve records a decision and audits it.
        var approved = await PostJsonAsync(
            $"/api/v1/admin/field-workflows/submissions/{submissionId}/decision",
            new FieldReviewDecisionRequest { Status = FieldReviewStatus.Approved, Note = "Looks good." },
            FieldReviewJsonContext.Default.FieldReviewDecisionRequest,
            FieldReviewJsonContext.Default.FieldReviewState);
        approved.Status.Should().Be(FieldReviewStatus.Approved);
        approved.DecidedBy.Should().NotBeNullOrEmpty();
        approved.DecidedAt.Should().NotBeNull();

        // The approval decision is recorded in the append-only audit log.
        var auditCount = await CountAuditAsync(submissionId, "field.review.decision.approved");
        auditCount.Should().BeGreaterThanOrEqualTo(1);
    }

    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("GET /api/v1/admin/field-workflows/submissions")]
    public async Task List_WithReviewStatusFilter_ReturnsOnlyMatching()
    {
        var pending = await SeedSubmissionAsync("filter-pending", "Pending record");
        var rejected = await SeedSubmissionAsync("filter-rejected", "Rejected record");

        var rejectResponse = await _fixture.Client.PostAsync(
            $"/api/v1/admin/field-workflows/submissions/{rejected}/decision",
            JsonContent(new FieldReviewDecisionRequest { Status = FieldReviewStatus.Rejected },
                FieldReviewJsonContext.Default.FieldReviewDecisionRequest));
        rejectResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var rejectedList = await GetJsonAsync(
            $"/api/v1/admin/field-workflows/submissions?reviewStatus={FieldReviewStatus.Rejected}",
            FieldReviewJsonContext.Default.FieldSubmissionListResult);
        rejectedList.Items.Should().Contain(item => item.SubmissionId == rejected);
        rejectedList.Items.Should().NotContain(item => item.SubmissionId == pending);
    }

    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("POST /api/v1/admin/field-workflows/submissions/{submissionId}/decision")]
    public async Task Decide_WithInvalidStatus_Returns400()
    {
        var submissionId = await SeedSubmissionAsync("invalid-status", "Record");
        var response = await _fixture.Client.PostAsync(
            $"/api/v1/admin/field-workflows/submissions/{submissionId}/decision",
            JsonContent(new FieldReviewDecisionRequest { Status = "pending" },
                FieldReviewJsonContext.Default.FieldReviewDecisionRequest));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("GET /api/v1/admin/field-workflows/submissions/{submissionId}")]
    public async Task Get_UnknownSubmission_Returns404()
    {
        var response = await _fixture.Client.GetAsync(
            $"/api/v1/admin/field-workflows/submissions/{Guid.NewGuid()}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Seeds a published form package and a completed (accepted) submission row
    /// directly through the shared <see cref="IFormPackageStore"/>. This exercises
    /// the same durable schema the runtime submission path writes to, without
    /// depending on the target service advertising edit capabilities.
    /// </summary>
    private async Task<Guid> SeedSubmissionAsync(string suffix, string name)
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IFormPackageStore>();

        var draft = await store.SaveDraftAsync(CreatePackage("Field review " + suffix), "test-admin");
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

    private async Task<long> CountAuditAsync(Guid submissionId, string action)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM honua.audit_log
            WHERE resource_type = 'field_submission'
              AND resource_id = @resource_id
              AND action = @action;
            """;
        command.Parameters.AddWithValue("resource_id", submissionId.ToString());
        command.Parameters.AddWithValue("action", action);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private async Task<T> GetJsonAsync<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> jsonTypeInfo)
    {
        var response = await _fixture.Client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await ReadJsonAsync(response, jsonTypeInfo);
    }

    private async Task<TResponse> PostJsonAsync<TRequest, TResponse>(
        string path,
        TRequest request,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TRequest> requestTypeInfo,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResponse> responseTypeInfo)
    {
        var response = await _fixture.Client.PostAsync(path, JsonContent(request, requestTypeInfo));
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return await ReadJsonAsync(response, responseTypeInfo);
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
