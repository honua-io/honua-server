// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using FluentAssertions;
using Honua.Ai.AnalysisContent;
using Honua.Core.Features.AnalysisContent.Domain;
using Honua.Core.Features.Forms.Packages;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Publishing.Content.Abstractions;
using Honua.Core.Features.Publishing.Content.Services;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Studio.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Studio;

/// <summary>
/// ADR-0069 (honua-server#3004) round-trip coverage for the bridged <c>form</c> and
/// <c>analysis</c> families: content created through the native APIs is enumerable and openable
/// through the Studio lifecycle, and drafts/versions saved through the lifecycle are visible
/// through the native APIs with family semantics (form JSON kinds, ruleIds, publish gate;
/// analysis artifact/job links) intact.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Studio)]
[Operation(Operations.StudioLifecycle)]
public sealed class StudioBridgedFamilyEndpointsTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions AnalysisJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly WebAppFixture _fixture = new WebAppFixture()
        .WithTestLicense(HonuaEdition.Pro)
        .ConfigureServices(services =>
        {
            // Studio drafts and the publication-badge join use the in-memory stores (matching
            // StudioPackageEndpointsTests: the studio/publication Postgres schema is not part of
            // the fixture template). The BRIDGED families still hit the real Postgres-backed
            // native stores (IFormPackageStore / IAnalysisContentStore), which is the surface
            // under test here.
            services.RemoveAll<IStudioPackageStore>();
            services.AddSingleton<IStudioPackageStore, InMemoryStudioPackageStore>();
            services.RemoveAll<IContentPublicationStore>();
            services.AddSingleton<IContentPublicationStore, InMemoryContentPublicationStore>();
        });

    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
        await EnableEditingCapabilitiesAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/studio/package-families")]
    public async Task PackageFamilies_AdvertiseBridgedNativeFormats()
    {
        var response = await _client.GetAsync("/api/v1/studio/package-families");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var capabilities = await ReadAsync<StudioPackageFamilyCapabilities>(
            response,
            StudioApiJsonContext.Default.ApiResponseStudioPackageFamilyCapabilities);

        capabilities.Families.Should().HaveCount(10);

        var form = capabilities.Families.Single(f => f.Family == StudioPackageFamily.Form);
        form.Format.Should().Be("honua.form-package.v1");
        form.PublishSupported.Should().BeTrue();
        form.SupportedOperations.Should().Contain(StudioPackageOperation.PublishRequestCreate);
        form.SupportedOperations.Should().NotContain(StudioPackageOperation.Rollback);
        form.Limitations.Should().Contain(l => l.Contains("native forms package store"));

        var analysis = capabilities.Families.Single(f => f.Family == StudioPackageFamily.Analysis);
        analysis.Format.Should().Be("honua.analysis-content.v1");
        analysis.PublishSupported.Should().BeFalse();
        analysis.SupportedOperations.Should().NotContain(StudioPackageOperation.PublishRequestCreate);
        analysis.SupportedOperations.Should().NotContain(StudioPackageOperation.Rollback);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/studio/content-items")]
    [Endpoint("GET /api/v1/studio/content-items/{itemId}/versions")]
    [Endpoint("GET /api/v1/studio/content-items/{itemId}/versions/{versionId}")]
    [Endpoint("POST /api/v1/studio/content-items/{itemId}/versions/{versionId}/reopen")]
    [Endpoint("PUT /api/v1/studio/package-drafts/{draftId}")]
    [Endpoint("POST /api/v1/studio/package-drafts/{draftId}/content-versions")]
    [Endpoint("POST /api/v1/studio/content-items/{itemId}/versions/{versionId}/publish-requests")]
    [Endpoint("POST /api/v1/studio/content-items/{itemId}/rollback-requests")]
    public async Task FormFamily_ConsoleAuthoredPackageRoundTripsLosslesslyThroughLifecycle()
    {
        // Author a form through the NATIVE console surface, exercising the family semantics the
        // bridge must preserve: a stable ruleId and JSON-kind-sensitive default value.
        var document = CreateFormDocument("Bridged inspection");
        var nativeDraft = await CreateNativeFormDraftAsync(document);
        nativeDraft.Version.Should().Be(1);

        // The native package is enumerable through the lifecycle content browser.
        var listResponse = await _client.GetAsync($"/api/v1/studio/content-items?family=form&q={nativeDraft.FormId}");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await ReadAsync<StudioContentItemListResponse>(
            listResponse,
            StudioApiJsonContext.Default.ApiResponseStudioContentItemListResponse);
        var row = list.Items.Should().ContainSingle(item => item.PackageKey == nativeDraft.FormId).Subject;
        row.Family.Should().Be(StudioPackageFamily.Form);
        row.State.Should().Be(StudioContentItemState.Current);
        row.CurrentVersionId.Should().NotBeNull();

        // Open the version through the lifecycle: the envelope body must be the native document,
        // byte-for-byte through the family's own serializer contract (REQ-002 / NFR-001).
        var versionsResponse = await _client.GetAsync($"/api/v1/studio/content-items/{row.ItemId:D}/versions");
        versionsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var versions = await ReadAsync<StudioContentVersionListResponse>(
            versionsResponse,
            StudioApiJsonContext.Default.ApiResponseStudioContentVersionListResponse);
        var version = versions.Versions.Should().ContainSingle().Subject;
        version.VersionNumber.Should().Be(1);
        version.Envelope.Format.Should().Be("honua.form-package.v1");
        version.ContentHash.Should().Be(nativeDraft.ContentHash);

        var nativeVersion = await GetNativeFormVersionAsync(nativeDraft.FormId, 1);
        JsonNode.DeepEquals(
                JsonNode.Parse(version.Envelope.Body!.Value.GetRawText()),
                JsonNode.Parse(JsonSerializer.Serialize(nativeVersion.Package, FormPackageJsonContext.Default.FormPackageDocument)))
            .Should().BeTrue("the lifecycle envelope body must round-trip the native form document losslessly");

        // Reopen through the lifecycle, edit the title, and save a new version.
        var reopenResponse = await _client.PostAsync(
            $"/api/v1/studio/content-items/{row.ItemId:D}/versions/{version.VersionId:D}/reopen",
            EmptyJson());
        reopenResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var draft = await ReadAsync<StudioPackageDraft>(
            reopenResponse,
            StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
        draft.Family.Should().Be(StudioPackageFamily.Form);

        var editedBody = JsonNode.Parse(draft.Envelope.Body!.Value.GetRawText())!;
        editedBody["title"] = "Bridged inspection (edited)";
        using var editedDocument = JsonDocument.Parse(editedBody.ToJsonString());
        var updateResponse = await PutAsync(
            $"/api/v1/studio/package-drafts/{draft.DraftId:D}",
            new UpdateStudioPackageDraftRequest
            {
                PackageKey = draft.PackageKey,
                Envelope = draft.Envelope with { Body = editedDocument.RootElement.Clone() },
                Generation = draft.Generation,
            },
            StudioApiJsonContext.Default.UpdateStudioPackageDraftRequest);
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var saveResponse = await PostAsync(
            $"/api/v1/studio/package-drafts/{draft.DraftId:D}/content-versions",
            new SaveStudioContentVersionRequest { ChangeNote = "lifecycle edit" },
            StudioApiJsonContext.Default.SaveStudioContentVersionRequest);
        saveResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var saved = await ReadAsync<StudioContentVersion>(
            saveResponse,
            StudioApiJsonContext.Default.ApiResponseStudioContentVersion);
        saved.VersionNumber.Should().Be(2);
        saved.ItemId.Should().Be(row.ItemId);

        // The NATIVE surface sees the lifecycle-authored version with family semantics intact:
        // new draft version, edited title, stable ruleId, and a numeric (not stringified)
        // default value.
        var nativeSaved = await GetNativeFormVersionAsync(nativeDraft.FormId, 2);
        nativeSaved.Status.Should().Be(FormPackageStatus.Draft);
        nativeSaved.Package.Title.Should().Be("Bridged inspection (edited)");
        var severityField = nativeSaved.Package.Fields.Single(field => field.FieldId == "severity");
        severityField.Validation.Single().RuleId.Should().Be("severity-range");
        severityField.DefaultValue!.Value.ValueKind.Should().Be(JsonValueKind.Number);

        // Publish through the lifecycle: runs the native publish validation gate and flips the
        // native version to published.
        var publishResponse = await _client.PostAsync(
            $"/api/v1/studio/content-items/{row.ItemId:D}/versions/{saved.VersionId:D}/publish-requests",
            EmptyJson());
        publishResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var publication = await ReadAsync<StudioPublicationRequest>(
            publishResponse,
            StudioApiJsonContext.Default.ApiResponseStudioPublicationRequest);
        publication.Status.Should().Be(StudioPublicationRequestStatus.Accepted);

        var nativePublished = await GetNativeFormVersionAsync(nativeDraft.FormId, 2);
        nativePublished.Status.Should().Be(FormPackageStatus.Published);

        // The lifecycle listing reflects the native published pointer.
        var publishedList = await ReadAsync<StudioContentItemListResponse>(
            await _client.GetAsync($"/api/v1/studio/content-items?family=form&q={nativeDraft.FormId}"),
            StudioApiJsonContext.Default.ApiResponseStudioContentItemListResponse);
        publishedList.Items.Single(item => item.PackageKey == nativeDraft.FormId)
            .State.Should().Be(StudioContentItemState.Published);

        // Rollback is a documented unsupported divergence for bridged forms.
        var rollbackResponse = await PostAsync(
            $"/api/v1/studio/content-items/{row.ItemId:D}/rollback-requests",
            new CreateStudioRollbackRequest { TargetVersionId = version.VersionId },
            StudioApiJsonContext.Default.CreateStudioRollbackRequest);
        rollbackResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/package-drafts")]
    [Endpoint("POST /api/v1/studio/package-drafts/{draftId}/content-versions")]
    public async Task FormFamily_LifecycleCreatedPackageIsVisibleThroughNativeApi()
    {
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(
            CreateFormDocument("Lifecycle-authored form"),
            FormPackageJsonContext.Default.FormPackageDocument));
        var createResponse = await PostAsync(
            "/api/v1/studio/package-drafts",
            new CreateStudioPackageDraftRequest
            {
                PackageKey = $"lifecycle-form-{Guid.NewGuid():N}",
                Envelope = new StudioPackageEnvelope
                {
                    Family = StudioPackageFamily.Form,
                    SchemaVersion = "1.0",
                    Format = "honua.form-package.v1",
                    Body = body.RootElement.Clone(),
                },
            },
            StudioApiJsonContext.Default.CreateStudioPackageDraftRequest);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var draft = await ReadAsync<StudioPackageDraft>(
            createResponse,
            StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
        draft.Validation.Status.Should().Be(StudioPackageValidationStatus.Valid);

        var saveResponse = await PostAsync(
            $"/api/v1/studio/package-drafts/{draft.DraftId:D}/content-versions",
            new SaveStudioContentVersionRequest(),
            StudioApiJsonContext.Default.SaveStudioContentVersionRequest);
        saveResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var saved = await ReadAsync<StudioContentVersion>(
            saveResponse,
            StudioApiJsonContext.Default.ApiResponseStudioContentVersion);
        saved.VersionNumber.Should().Be(1);

        // The lifecycle item id doubles as the native form id for lifecycle-created packages.
        var nativeResponse = await _client.GetAsync($"/api/v1/admin/forms/packages/{saved.ItemId:D}");
        nativeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var native = await ReadNativeJsonAsync(nativeResponse, FormPackageJsonContext.Default.FormPackageVersion);
        native.Status.Should().Be(FormPackageStatus.Draft);
        native.Package.Title.Should().Be("Lifecycle-authored form");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/studio/content-items")]
    [Endpoint("GET /api/v1/studio/content-items/{itemId}/versions")]
    [Endpoint("POST /api/v1/studio/content-items/{itemId}/versions/{versionId}/reopen")]
    [Endpoint("POST /api/v1/studio/package-drafts/{draftId}/content-versions")]
    [Endpoint("POST /api/v1/studio/content-items/{itemId}/versions/{versionId}/publish-requests")]
    public async Task AnalysisFamily_NativeContentRoundTripsWithJobAndArtifactLinks()
    {
        // Author an analysis package through the NATIVE analysis content surface, then add a
        // second version carrying job/artifact provenance links.
        var name = $"bridged-analysis-{Guid.NewGuid():N}";
        var content = CreateAnalysisPackageContent();
        var createResponse = await _client.PostAsJsonAsync(
            "/api/v1/analysis/content/items",
            new CreateAnalysisContentItemRequest
            {
                Kind = AnalysisContentKind.AnalysisPackage,
                Name = name,
                AnalysisPackage = content,
            },
            AnalysisJsonOptions);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = (await createResponse.Content.ReadFromJsonAsync<AnalysisContentItemResponse>(AnalysisJsonOptions))!;

        var addVersionResponse = await _client.PostAsJsonAsync(
            $"/api/v1/analysis/content/items/{created.Item.ItemId}/versions",
            new CreateAnalysisContentVersionRequest
            {
                AnalysisPackage = content,
                CreatedFromJobId = "job-bridge-123",
                CreatedFromArtifactIds = ["artifact-bridge-1"],
            },
            AnalysisJsonOptions);
        addVersionResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        // Enumerable and openable through the lifecycle.
        var list = await ReadAsync<StudioContentItemListResponse>(
            await _client.GetAsync($"/api/v1/studio/content-items?family=analysis&q={name}"),
            StudioApiJsonContext.Default.ApiResponseStudioContentItemListResponse);
        var row = list.Items.Should().ContainSingle(item => item.PackageKey == name).Subject;
        row.Family.Should().Be(StudioPackageFamily.Analysis);

        var versions = await ReadAsync<StudioContentVersionListResponse>(
            await _client.GetAsync($"/api/v1/studio/content-items/{row.ItemId:D}/versions"),
            StudioApiJsonContext.Default.ApiResponseStudioContentVersionListResponse);
        versions.Versions.Should().HaveCount(2);
        var second = versions.Versions.Single(v => v.VersionNumber == 2);
        second.Envelope.Format.Should().Be("honua.analysis-content.v1");
        second.Dependencies.Should().Contain(d => d.Kind == "job" && d.Ref == "job-bridge-123");
        second.Dependencies.Should().Contain(d => d.Kind == "artifact" && d.Ref == "artifact-bridge-1");

        // Reopen and save through the lifecycle: the native surface sees version 3 with the
        // plan and provenance links preserved.
        var reopenResponse = await _client.PostAsync(
            $"/api/v1/studio/content-items/{row.ItemId:D}/versions/{second.VersionId:D}/reopen",
            EmptyJson());
        reopenResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var draft = await ReadAsync<StudioPackageDraft>(
            reopenResponse,
            StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);

        var saveResponse = await PostAsync(
            $"/api/v1/studio/package-drafts/{draft.DraftId:D}/content-versions",
            new SaveStudioContentVersionRequest { ChangeNote = "lifecycle save" },
            StudioApiJsonContext.Default.SaveStudioContentVersionRequest);
        saveResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var saved = await ReadAsync<StudioContentVersion>(
            saveResponse,
            StudioApiJsonContext.Default.ApiResponseStudioContentVersion);
        saved.VersionNumber.Should().Be(3);
        saved.Dependencies.Should().Contain(d => d.Kind == "job" && d.Ref == "job-bridge-123");

        var latestResponse = await _client.GetAsync($"/api/v1/analysis/content/items/{created.Item.ItemId}/versions/latest");
        latestResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var latest = (await latestResponse.Content.ReadFromJsonAsync<AnalysisContentVersionResponse>(AnalysisJsonOptions))!;
        latest.Version.Version.Should().Be(3);
        latest.Version.CreatedFromJobId.Should().Be("job-bridge-123");
        latest.Version.CreatedFromArtifactIds.Should().ContainSingle(id => id == "artifact-bridge-1");
        latest.Version.AnalysisPackage!.Plan.PlanId.Should().Be(content.Plan.PlanId);

        // Publish-request is a documented unsupported divergence for the bridged analysis
        // family (route publication belongs to the Content Publication Registry).
        var publishResponse = await _client.PostAsync(
            $"/api/v1/studio/content-items/{row.ItemId:D}/versions/{saved.VersionId:D}/publish-requests",
            EmptyJson());
        publishResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    private async Task EnableEditingCapabilitiesAsync()
    {
        // Mirrors FormPackageEndpointsTests: the form publish gate validates the target
        // service's editing capabilities through the Metadata v2 graph.
        await _fixture.Postgres.ApplyGlobalSeedSqlAsync("""
            UPDATE honua.services
            SET capabilities = ARRAY['Query', 'Extract', 'Create', 'Update', 'Delete']
            WHERE service_name = 'test';
            """, _fixture.CurrentSchema);
        _fixture.EnableV2ServiceEditingCapabilities(
            "test",
            ["Query", "Extract", "Create", "Update", "Delete"]);
    }

    private async Task<FormPackageVersion> CreateNativeFormDraftAsync(FormPackageDocument document)
    {
        var response = await _client.PostAsync(
            "/api/v1/admin/forms/packages",
            new StringContent(
                JsonSerializer.Serialize(document, FormPackageJsonContext.Default.FormPackageDocument),
                Encoding.UTF8,
                "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return await ReadNativeJsonAsync(response, FormPackageJsonContext.Default.FormPackageVersion);
    }

    private async Task<FormPackageVersion> GetNativeFormVersionAsync(string formId, int version)
    {
        var response = await _client.GetAsync($"/api/v1/admin/forms/packages/{formId}/versions/{version}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await ReadNativeJsonAsync(response, FormPackageJsonContext.Default.FormPackageVersion);
    }

    private static FormPackageDocument CreateFormDocument(string title) => new()
    {
        Title = title,
        Target = new FormTargetDefinition
        {
            ServiceId = WebAppFixture.TestServiceId,
            LayerId = WebAppFixture.TestLayerId,
        },
        Sections =
        [
            new FormSectionDefinition { SectionId = "main", Label = "Main", FieldIds = ["name", "severity"] },
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
                SectionId = "main",
            },
            new FormFieldDefinition
            {
                FieldId = "severity",
                Label = "Severity",
                Type = "number",
                TargetField = "rating",
                SectionId = "main",
                DefaultValue = ParseElement("3"),
                Validation =
                [
                    new FormValidationRule
                    {
                        RuleId = "severity-range",
                        Type = "max",
                        Message = "Severity must be 5 or lower.",
                        Parameters = ParseElement("""{"max":5}"""),
                    },
                ],
            },
        ],
        SubmitPolicy = new FormSubmitPolicy
        {
            AllowedOperations = [FormSubmissionOperations.Create],
            RequiresGeometry = true,
        },
        OfflinePolicy = new FormOfflinePolicy { Enabled = true },
    };

    private static AnalysisPackageContent CreateAnalysisPackageContent() => new()
    {
        Intent = new AnalysisIntent
        {
            IntentId = "intent-bridge",
            Goal = "buffer incidents",
            RequestedOutputs = [ArtifactKind.FeatureLayer],
        },
        Plan = new AnalysisPlan
        {
            PlanId = "plan-bridge",
            IntentId = "intent-bridge",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "query-1",
                    Kind = AnalysisPlanStepKind.QueryFeatures,
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = WebAppFixture.TestLayerId.ToString(CultureInfo.InvariantCulture),
                    },
                },
                new AnalysisPlanStep
                {
                    StepId = "buffer-1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "geometry.buffer",
                    Inputs = new Dictionary<string, string> { ["distance"] = "10" },
                    DependsOn = ["query-1"],
                },
            ],
            Outputs = [ArtifactKind.FeatureLayer],
        },
        RequestedArtifacts = [ArtifactKind.FeatureLayer],
        SpatialReferenceId = 4326,
        Units = "meters",
    };

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private async Task<HttpResponseMessage> PostAsync<T>(string path, T body, JsonTypeInfo<T> typeInfo)
        => await _client.PostAsync(path, JsonContent(body, typeInfo));

    private async Task<HttpResponseMessage> PutAsync<T>(string path, T body, JsonTypeInfo<T> typeInfo)
        => await _client.PutAsync(path, JsonContent(body, typeInfo));

    private static StringContent JsonContent<T>(T body, JsonTypeInfo<T> typeInfo)
        => new(JsonSerializer.Serialize(body, typeInfo), Encoding.UTF8, "application/json");

    private static StringContent EmptyJson()
        => new("{}", Encoding.UTF8, "application/json");

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<ApiResponse<T>> typeInfo)
    {
        var json = await response.Content.ReadAsStringAsync();
        var envelope = JsonSerializer.Deserialize(json, typeInfo);
        envelope.Should().NotBeNull();
        envelope!.Success.Should().BeTrue();
        envelope.Data.Should().NotBeNull();
        return envelope.Data!;
    }

    private static async Task<T> ReadNativeJsonAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> typeInfo)
    {
        var json = await response.Content.ReadAsStringAsync();
        var value = JsonSerializer.Deserialize(json, typeInfo);
        value.Should().NotBeNull();
        return value!;
    }
}
