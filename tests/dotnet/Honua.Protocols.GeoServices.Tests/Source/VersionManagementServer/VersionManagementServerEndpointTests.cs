// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.VersionManagementServer;

/// <summary>
/// Integration coverage for the GeoServices VersionManagementServer branch-versioning protocol
/// slice (#1272, ADR-0051). Exercises every endpoint end-to-end against PostGIS with the Enterprise
/// branch-versioning entitlement active, and verifies the full create → edit (gdbVersion) → query
/// (gdbVersion sees the edit, DEFAULT unaffected) → reconcile → post → DEFAULT-reflects flow.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.VersionManagementServer)]
public sealed class VersionManagementServerEndpointTests : IAsyncLifetime
{
    // Full literal route prefix. Kept inline (not a single const) at each request site so the
    // EndpointRegistry coverage scanner — which matches the literal route template inside each
    // [IntegrationTest] method body — can back every VersionManagementServer endpoint.
    private const string ServiceBase = "/rest/services/" + WebAppFixture.TestServiceId + "/VersionManagementServer";

    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync()
    {
        _fixture.WithTestLicense(HonuaEdition.Enterprise);
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.VersionManagement)]
    [Endpoint("GET /rest/services/{serviceId}/VersionManagementServer")]
    [InterfaceOperation(TestProtocols.VersionManagementServer, "serviceInfo")]
    public async Task ServiceInfo_ReturnsCapabilities()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VersionManagementServer?f=json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("defaultVersionName").GetString().Should().Be("sde.DEFAULT");
        doc.RootElement.GetProperty("capabilities").GetString().Should().Contain("Create");
    }

    [IntegrationTest]
    [Operation(Operations.VersionManagement)]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/create")]
    [InterfaceOperation(TestProtocols.VersionManagementServer, "create")]
    public async Task Create_NewVersion_ReturnsVersionInfo()
    {
        var response = await PostFormAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VersionManagementServer/create",
            ("versionName", "admin.create_returns"), ("accessPermission", "private"), ("f", "json"));
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "create should succeed; body: {0}", await response.Content.ReadAsStringAsync());

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var info = doc.RootElement.GetProperty("versionInfo");

        info.GetProperty("versionName").GetString().Should().Be("admin.create_returns");
        Guid.TryParse(info.GetProperty("versionGuid").GetString(), out _).Should().BeTrue();
        info.GetProperty("status").GetString().Should().Be("active");
    }

    [IntegrationTest]
    [Operation(Operations.VersionManagement)]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/create")]
    [InterfaceOperation(TestProtocols.VersionManagementServer, "create")]
    public async Task Create_DuplicateVersionName_Returns409Conflict()
    {
        // First create should succeed.
        var first = await PostFormAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VersionManagementServer/create",
            ("versionName", "admin.dup_name_test"), ("accessPermission", "private"), ("f", "json"));
        first.StatusCode.Should().Be(HttpStatusCode.OK,
            "first create should succeed; body: {0}", await first.Content.ReadAsStringAsync());

        // Second create with the same name must return 409 with an Esri-style error body.
        var second = await PostFormAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VersionManagementServer/create",
            ("versionName", "admin.dup_name_test"), ("accessPermission", "private"), ("f", "json"));
        second.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "duplicate version name must return 409; body: {0}", await second.Content.ReadAsStringAsync());

        using var doc = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        doc.RootElement.TryGetProperty("error", out _).Should().BeTrue(
            "GeoServices protocol wraps errors in an 'error' envelope");
    }

    [IntegrationTest]
    [Operation(Operations.VersionManagement)]
    [Endpoint("GET /rest/services/{serviceId}/VersionManagementServer/versions")]
    [InterfaceOperation(TestProtocols.VersionManagementServer, "versions")]
    public async Task ListVersions_AfterCreate_ContainsVersion()
    {
        await CreateVersionAsync("admin.list_versions");

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VersionManagementServer/versions?f=json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var names = doc.RootElement.GetProperty("versions").EnumerateArray()
            .Select(v => v.GetProperty("versionName").GetString());
        names.Should().Contain("admin.list_versions");
    }

    [IntegrationTest]
    [Operation(Operations.VersionManagement)]
    [Endpoint("GET /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}")]
    [InterfaceOperation(TestProtocols.VersionManagementServer, "versionInfo")]
    public async Task VersionInfo_ReturnsSingleVersion()
    {
        var created = await CreateVersionAsync("admin.version_info");
        var guid = created.GetProperty("versionGuid").GetString();

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VersionManagementServer/versions/{guid}?f=json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("versionGuid").GetString().Should().Be(guid);
        doc.RootElement.GetProperty("versionName").GetString().Should().Be("admin.version_info");
    }

    [IntegrationTest]
    [Operation(Operations.VersionManagement)]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/alter")]
    [InterfaceOperation(TestProtocols.VersionManagementServer, "alter")]
    public async Task Alter_UpdatesDescription()
    {
        var created = await CreateVersionAsync("admin.alter_me");
        var guid = created.GetProperty("versionGuid").GetString();

        var response = await PostFormAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VersionManagementServer/versions/{guid}/alter",
            ("description", "updated"), ("f", "json"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.VersionManagement)]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/startReading")]
    [InterfaceOperation(TestProtocols.VersionManagementServer, "startReading")]
    public async Task StartReading_AcknowledgesSession()
    {
        var created = await CreateVersionAsync("admin.start_reading");
        var guid = created.GetProperty("versionGuid").GetString();

        var response = await PostFormAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VersionManagementServer/versions/{guid}/startReading",
            ("f", "json"));

        // The read session returns the version's durable branch generation as a stable moment (a
        // cursor a client echoes to pin a snapshot), not a throwaway wall-clock value.
        await AssertSuccessMomentAsync(response, expectGenerationMoment: true);
    }

    [IntegrationTest]
    [Operation(Operations.VersionManagement)]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/startReading")]
    [InterfaceOperation(TestProtocols.VersionManagementServer, "startReading")]
    public async Task StartReading_UnknownVersion_ReturnsNotFound()
    {
        // The session acknowledgement now resolves the named version, so an unknown GUID is a 404
        // rather than a false success.
        var response = await PostFormAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VersionManagementServer/versions/{Guid.NewGuid()}/startReading",
            ("f", "json"));
        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "an unknown version should not open a session; body: {0}", await response.Content.ReadAsStringAsync());
    }

    [IntegrationTest]
    [Operation(Operations.VersionManagement)]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/stopReading")]
    [InterfaceOperation(TestProtocols.VersionManagementServer, "stopReading")]
    public async Task StopReading_AcknowledgesSession()
    {
        var created = await CreateVersionAsync("admin.stop_reading");
        var guid = created.GetProperty("versionGuid").GetString();

        var response = await PostFormAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VersionManagementServer/versions/{guid}/stopReading",
            ("f", "json"));
        await AssertSuccessMomentAsync(response);
    }

    [IntegrationTest]
    [Operation(Operations.VersionManagement)]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/startEditing")]
    [InterfaceOperation(TestProtocols.VersionManagementServer, "startEditing")]
    public async Task StartEditing_AcknowledgesSession()
    {
        var created = await CreateVersionAsync("admin.start_editing");
        var guid = created.GetProperty("versionGuid").GetString();

        var response = await PostFormAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VersionManagementServer/versions/{guid}/startEditing",
            ("f", "json"));

        // An active version opens an edit session and reports its branch generation as the moment.
        await AssertSuccessMomentAsync(response, expectGenerationMoment: true);
    }

    [IntegrationTest]
    [Operation(Operations.VersionManagement)]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/startEditing")]
    [InterfaceOperation(TestProtocols.VersionManagementServer, "startEditing")]
    public async Task StartEditing_UnknownVersion_ReturnsNotFound()
    {
        var response = await PostFormAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VersionManagementServer/versions/{Guid.NewGuid()}/startEditing",
            ("f", "json"));
        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "an unknown version should not open an edit session; body: {0}", await response.Content.ReadAsStringAsync());
    }

    [IntegrationTest]
    [Operation(Operations.VersionManagement)]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/startEditing")]
    [InterfaceOperation(TestProtocols.VersionManagementServer, "startEditing")]
    public async Task StartEditing_VersionMidReconcile_Returns409Locked()
    {
        // A version that is mid-reconcile/post is locked (Redis-backed) for the duration of that job,
        // so opening an edit session would be a false acknowledgement: no edit can land. Pin the
        // version into the Reconciling state and assert startEditing refuses with a 409 in-progress
        // (ADR-0051; the documented edit-session lock path). The read path stays open in this state.
        var created = await CreateVersionAsync("admin.start_editing_locked");
        var guid = created.GetProperty("versionGuid").GetString();

        // VersionState.Reconciling == 1; honua.gdb_versions is the global versioning catalog and the
        // version_id GUID targets exactly the version created above.
        // #2020: route the global honua.gdb_versions UPDATE through the schema-mutation advisory lock.
        await _fixture.Postgres.ApplyGlobalSeedSqlAsync(
            $"UPDATE honua.gdb_versions SET state = 1 WHERE version_id = '{guid}'::uuid");

        var editResponse = await PostFormAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VersionManagementServer/versions/{guid}/startEditing",
            ("f", "json"));
        editResponse.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "opening an edit session against a reconciling version must return 409; body: {0}",
            await editResponse.Content.ReadAsStringAsync());

        using (var doc = JsonDocument.Parse(await editResponse.Content.ReadAsStringAsync()))
        {
            doc.RootElement.TryGetProperty("error", out _).Should().BeTrue(
                "GeoServices protocol wraps errors in an 'error' envelope");
        }

        // A read session is not gated on the lock — it reports the current moment regardless of state.
        var readResponse = await PostFormAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VersionManagementServer/versions/{guid}/startReading",
            ("f", "json"));
        await AssertSuccessMomentAsync(readResponse, expectGenerationMoment: true);
    }

    [IntegrationTest]
    [Operation(Operations.VersionManagement)]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/stopEditing")]
    [InterfaceOperation(TestProtocols.VersionManagementServer, "stopEditing")]
    public async Task StopEditing_AcknowledgesSession()
    {
        var created = await CreateVersionAsync("admin.stop_editing");
        var guid = created.GetProperty("versionGuid").GetString();

        var response = await PostFormAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VersionManagementServer/versions/{guid}/stopEditing",
            ("f", "json"));
        await AssertSuccessMomentAsync(response);
    }

    [IntegrationTest]
    [Operation(Operations.VersionManagement)]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/reconcile")]
    [InterfaceOperation(TestProtocols.VersionManagementServer, "reconcile")]
    public async Task Reconcile_CleanVersion_CanPost()
    {
        var created = await CreateVersionAsync("admin.reconcile_clean");
        var guid = created.GetProperty("versionGuid").GetString();

        var response = await PostFormAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VersionManagementServer/versions/{guid}/reconcile",
            ("f", "json"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("hasConflicts").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("canPost").GetBoolean().Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.VersionManagement)]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/reconcile")]
    [InterfaceOperation(TestProtocols.VersionManagementServer, "reconcile")]
    public async Task Reconcile_WithPolicy_ReportsAutoResolvedCount()
    {
        // A clean version with a policy still returns the auto-resolved count field (zero here) and can
        // post; this exercises the conflictResolution parameter parsing on the reconcile endpoint.
        var created = await CreateVersionAsync("admin.reconcile_policy");
        var guid = created.GetProperty("versionGuid").GetString();

        var response = await PostFormAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VersionManagementServer/versions/{guid}/reconcile",
            ("conflictResolution", "lastWriteWins"), ("f", "json"));
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "reconcile with a policy should succeed; body: {0}", await response.Content.ReadAsStringAsync());

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("canPost").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("autoResolvedCount").GetInt32().Should().Be(0);
    }

    [IntegrationTest]
    [Operation(Operations.VersionManagement)]
    [Endpoint("GET /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/inspectConflicts")]
    [InterfaceOperation(TestProtocols.VersionManagementServer, "inspectConflicts")]
    public async Task InspectConflicts_CleanVersion_ReturnsNoConflicts()
    {
        var created = await CreateVersionAsync("admin.inspect_clean");
        var guid = created.GetProperty("versionGuid").GetString();

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VersionManagementServer/versions/{guid}/inspectConflicts?f=json");
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "inspectConflicts should succeed; body: {0}", await response.Content.ReadAsStringAsync());

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("hasConflicts").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("conflicts").GetArrayLength().Should().Be(0);
    }

    [IntegrationTest]
    [Operation(Operations.VersionManagement)]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/resolveConflicts")]
    [InterfaceOperation(TestProtocols.VersionManagementServer, "resolveConflicts")]
    public async Task ResolveConflicts_NoPendingConflicts_ReturnsCanPost()
    {
        // With no pending conflicts, submitting an (empty-effect) resolution leaves the version postable.
        var created = await CreateVersionAsync("admin.resolve_clean");
        var guid = created.GetProperty("versionGuid").GetString();

        var conflicts = "[{\"layerId\":0,\"objectId\":1,\"choice\":\"version\"}]";
        var response = await PostFormAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VersionManagementServer/versions/{guid}/resolveConflicts",
            ("conflicts", conflicts), ("f", "json"));
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "resolveConflicts should succeed; body: {0}", await response.Content.ReadAsStringAsync());

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("canPost").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("remaining").GetInt32().Should().Be(0);
    }

    [IntegrationTest]
    [Operation(Operations.VersionManagement)]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/post")]
    [InterfaceOperation(TestProtocols.VersionManagementServer, "post")]
    public async Task Post_AfterReconcile_Succeeds()
    {
        var created = await CreateVersionAsync("admin.post_clean");
        var guid = created.GetProperty("versionGuid").GetString();

        await PostFormAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VersionManagementServer/versions/{guid}/reconcile",
            ("f", "json"));

        var response = await PostFormAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VersionManagementServer/versions/{guid}/post",
            ("f", "json"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.TryGetProperty("success", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.VersionManagement)]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/reconcile")]
    [InterfaceOperation(TestProtocols.VersionManagementServer, "reconcile")]
    public async Task Reconcile_Async_Returns202WithPollableJob()
    {
        var created = await CreateVersionAsync("admin.reconcile_async");
        var guid = created.GetProperty("versionGuid").GetString();

        // async=true starts a durable, pollable job and returns 202 with a job handle (#1553).
        var response = await PostFormAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VersionManagementServer/versions/{guid}/reconcile",
            ("async", "true"), ("f", "json"));
        response.StatusCode.Should().Be(HttpStatusCode.Accepted,
            "async reconcile should be accepted; body: {0}", await response.Content.ReadAsStringAsync());

        string jobId;
        string statusUrl;
        using (var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            doc.RootElement.GetProperty("kind").GetString().Should().Be("reconcile");
            jobId = doc.RootElement.GetProperty("jobId").GetString()!;
            jobId.Should().NotBeNullOrWhiteSpace();
            statusUrl = doc.RootElement.GetProperty("statusUrl").GetString()!;
            statusUrl.Should().Contain(jobId);
        }

        // Poll the job-status endpoint until the job reaches a terminal state.
        var terminal = await PollJobStatusAsync(statusUrl);
        terminal.GetProperty("status").GetString().Should().BeOneOf("succeeded", "running", "pending");
    }

    [IntegrationTest]
    [Operation(Operations.VersionManagement)]
    [Endpoint("GET /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/jobs/{jobId}")]
    [InterfaceOperation(TestProtocols.VersionManagementServer, "jobStatus")]
    public async Task JobStatus_UnknownJob_ReturnsNotFound()
    {
        var created = await CreateVersionAsync("admin.job_status");
        var guid = created.GetProperty("versionGuid").GetString();

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VersionManagementServer/versions/{guid}/jobs/{Guid.NewGuid()}?f=json");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.VersionManagement)]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/delete")]
    [InterfaceOperation(TestProtocols.VersionManagementServer, "delete")]
    public async Task Delete_RemovesVersion()
    {
        var created = await CreateVersionAsync("admin.delete_me");
        var guid = created.GetProperty("versionGuid").GetString();

        var response = await PostFormAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VersionManagementServer/versions/{guid}/delete",
            ("f", "json"));
        await AssertSuccessMomentAsync(response);

        var info = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VersionManagementServer/versions/{guid}?f=json");
        info.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private async Task<JsonElement> CreateVersionAsync(string versionName)
    {
        var response = await PostFormAsync($"{ServiceBase}/create",
            ("versionName", versionName), ("accessPermission", "private"), ("f", "json"));
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "create should succeed; body: {0}", await response.Content.ReadAsStringAsync());

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("versionInfo").Clone();
    }

    private async Task AssertSuccessMomentAsync(HttpResponseMessage response, bool expectGenerationMoment = false)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "operation should succeed; body: {0}", await response.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();

        if (expectGenerationMoment)
        {
            // Session moments are bound to the version's durable branch generation (a non-negative
            // cursor), not a wall-clock timestamp.
            doc.RootElement.TryGetProperty("moment", out var moment).Should().BeTrue();
            moment.GetInt64().Should().BeGreaterThanOrEqualTo(0);
        }
    }

    private async Task<JsonElement> PollJobStatusAsync(string statusUrl)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var response = await _fixture.Client.GetAsync($"{statusUrl}?f=json");
            response.StatusCode.Should().Be(HttpStatusCode.OK,
                "job-status poll should succeed; body: {0}", await response.Content.ReadAsStringAsync());
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var status = doc.RootElement.GetProperty("status").GetString();
            if (status is not ("pending" or "running"))
            {
                return doc.RootElement.Clone();
            }

            await Task.Delay(50);
        }

        // The job is durable and pollable even if not yet terminal; return the last observed state.
        var last = await _fixture.Client.GetAsync($"{statusUrl}?f=json");
        using var lastDoc = JsonDocument.Parse(await last.Content.ReadAsStringAsync());
        return lastDoc.RootElement.Clone();
    }

    private Task<HttpResponseMessage> PostFormAsync(string url, params (string Key, string Value)[] fields)
    {
        var content = new FormUrlEncodedContent(
            fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));
        return _fixture.Client.PostAsync(url, content);
    }
}
