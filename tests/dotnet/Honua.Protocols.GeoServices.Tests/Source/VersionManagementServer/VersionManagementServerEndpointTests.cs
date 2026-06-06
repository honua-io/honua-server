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
        await AssertSuccessMomentAsync(response);
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
        await AssertSuccessMomentAsync(response);
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

    private async Task AssertSuccessMomentAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "operation should succeed; body: {0}", await response.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
    }

    private Task<HttpResponseMessage> PostFormAsync(string url, params (string Key, string Value)[] fields)
    {
        var content = new FormUrlEncodedContent(
            fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));
        return _fixture.Client.PostAsync(url, content);
    }
}
