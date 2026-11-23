// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FieldWorkflows.Review;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.FieldWorkflows;

// Validation runs before the store, so an unknown submission id answers 404 only when the input
// passed and 400 when it was refused. That lets the limit tests run without seeding a submission.
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class FieldWorkflowInputLimitsTests : IAsyncLifetime
{
    private const string SubmissionsPath = "/api/v1/admin/field-workflows/submissions";
    private const string ExportsPath = "/api/v1/admin/field-workflows/exports";
    private const int MaxNameLength = 256;
    private const int MaxTextLength = 4000;

    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [Operation(Operations.Configuration)]
    [Endpoint("POST /api/v1/admin/field-workflows/submissions/{submissionId}/assignment")]
    [Endpoint("POST /api/v1/admin/field-workflows/submissions/{submissionId}/decision")]
    [Endpoint("POST /api/v1/admin/field-workflows/submissions/{submissionId}/comments")]
    [InlineData("assignment", "assignedTo", "reviewer\u0000one")]
    [InlineData("assignment", "assignedTo", "reviewer\r\nforged")]
    [InlineData("decision", "note", "looks\u0000good")]
    [InlineData("comments", "body", "retake\u0000photo")]
    public async Task ReviewWrite_UnstorableText_Returns400(string action, string field, string value)
    {
        using var response = await PostReviewAsync(action, field, value);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
    }

    [IntegrationTheory]
    [Operation(Operations.Configuration)]
    [Endpoint("POST /api/v1/admin/field-workflows/submissions/{submissionId}/assignment")]
    [Endpoint("POST /api/v1/admin/field-workflows/submissions/{submissionId}/decision")]
    [Endpoint("POST /api/v1/admin/field-workflows/submissions/{submissionId}/comments")]
    [InlineData("assignment", "assignedTo", MaxNameLength + 1)]
    [InlineData("decision", "note", MaxTextLength + 1)]
    [InlineData("comments", "body", MaxTextLength + 1)]
    public async Task ReviewWrite_OverLimit_Returns400(string action, string field, int length)
    {
        using var response = await PostReviewAsync(action, field, new string('r', length));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
    }

    [IntegrationTheory]
    [Operation(Operations.Configuration)]
    [Endpoint("POST /api/v1/admin/field-workflows/submissions/{submissionId}/assignment")]
    [Endpoint("POST /api/v1/admin/field-workflows/submissions/{submissionId}/decision")]
    [Endpoint("POST /api/v1/admin/field-workflows/submissions/{submissionId}/comments")]
    [InlineData("assignment", "assignedTo", MaxNameLength)]
    [InlineData("decision", "note", MaxTextLength)]
    [InlineData("comments", "body", MaxTextLength)]
    public async Task ReviewWrite_AtLimit_PassesValidation(string action, string field, int length)
    {
        using var response = await PostReviewAsync(action, field, new string('r', length));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync());
    }

    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("POST /api/v1/admin/field-workflows/submissions/{submissionId}/comments")]
    public async Task Comment_WithLineBreaksAndTab_PassesValidation()
    {
        using var response = await PostReviewAsync("comments", "body", "Line one\r\nLine two\tend");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync());
    }

    [IntegrationTheory]
    [Operation(Operations.Configuration)]
    [Endpoint("GET /api/v1/admin/field-workflows/submissions")]
    [InlineData("formId=form%00one")]
    [InlineData("serviceId=svc%00one")]
    [InlineData("assignedTo=reviewer%00one")]
    [InlineData("syncStatus=accepted")]
    [InlineData("syncStatus=Synced")]
    [InlineData("submittedFrom=2026-11-02T00:00:00Z&submittedTo=2026-11-01T00:00:00Z")]
    public async Task List_InvalidFilter_Returns400(string query)
    {
        using var response = await _fixture.Client.GetAsync($"{SubmissionsPath}?{query}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
    }

    [IntegrationTheory]
    [Operation(Operations.Configuration)]
    [Endpoint("GET /api/v1/admin/field-workflows/submissions")]
    [InlineData(FieldSyncStatus.Synced)]
    [InlineData(FieldSyncStatus.Rejected)]
    [InlineData(FieldSyncStatus.Failed)]
    [InlineData(FieldSyncStatus.Pending)]
    public async Task List_KnownSyncStatus_Returns200(string syncStatus)
    {
        using var response = await _fixture.Client.GetAsync($"{SubmissionsPath}?syncStatus={syncStatus}");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [IntegrationTheory]
    [Operation(Operations.Configuration)]
    [Endpoint("POST /api/v1/admin/field-workflows/exports")]
    [InlineData("formId", "form\u0000one")]
    [InlineData("serviceId", "svc\u0000one")]
    [InlineData("assignedTo", "reviewer\u0000one")]
    [InlineData("syncStatus", "accepted")]
    public async Task Export_InvalidFilter_Returns400(string field, string value)
    {
        using var content = Json(new Dictionary<string, string> { [field] = value });
        using var response = await _fixture.Client.PostAsync(ExportsPath, content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
    }

    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("POST /api/v1/admin/field-workflows/exports")]
    public async Task Export_InvertedDateRange_Returns400()
    {
        using var content = Json(new Dictionary<string, string>
        {
            ["submittedFrom"] = "2026-11-02T00:00:00Z",
            ["submittedTo"] = "2026-11-01T00:00:00Z",
        });
        using var response = await _fixture.Client.PostAsync(ExportsPath, content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
    }

    private async Task<HttpResponseMessage> PostReviewAsync(string action, string field, string value)
    {
        var body = new Dictionary<string, object> { [field] = value };
        if (action == "decision")
        {
            body["status"] = FieldReviewStatus.Approved;
        }

        using var content = Json(body);
        return await _fixture.Client.PostAsync($"{SubmissionsPath}/{Guid.NewGuid()}/{action}", content);
    }

    private static StringContent Json<T>(T value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
}
