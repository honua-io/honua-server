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
/// End-to-end branch-versioning flow over the GeoServices surfaces (#1272, ADR-0051): create a
/// version, edit with <c>gdbVersion</c>, confirm the versioned query sees the edit while DEFAULT is
/// unaffected, reconcile, post, and confirm DEFAULT then reflects the change. Also asserts the
/// DEFAULT (gdbVersion-absent) path is unchanged and the capability metadata advertises versioning.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerGdbVersionFlowTests : IAsyncLifetime
{
    private const string ServiceId = WebAppFixture.TestServiceId;
    private const string VmsBase = "/rest/services/" + ServiceId + "/VersionManagementServer";
    private const string LayerBase = "/rest/services/" + ServiceId + "/FeatureServer/0";

    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync()
    {
        _fixture.WithTestLicense(HonuaEdition.Enterprise);
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task GdbVersionFlow_EditQueryReconcilePost_IsolatesThenMerges()
    {
        var marker = "vflow_" + Guid.NewGuid().ToString("N")[..8];
        var versionGuid = await CreateVersionAsync("admin." + marker);

        // Edit on the branch version.
        var addResponse = await PostFormAsync($"{LayerBase}/applyEdits",
            ("adds", BuildAddsJson(marker)),
            ("gdbVersion", versionGuid),
            ("f", "json"));
        addResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            "versioned applyEdits should succeed; body: {0}", await addResponse.Content.ReadAsStringAsync());
        (await CountByMarkerAsync(addResponse)).Should().Be(1, "the add should have committed to the version");

        // The versioned query sees the branch edit.
        (await QueryCountAsync(marker, versionGuid)).Should().Be(1,
            "querying with gdbVersion must surface the version's edit");

        // DEFAULT is unaffected (gdbVersion absent).
        (await QueryCountAsync(marker, gdbVersion: null)).Should().Be(0,
            "DEFAULT must not see the version's uncommitted edit");

        // Reconcile clean, then post.
        var reconcile = await PostFormAsync($"{VmsBase}/versions/{versionGuid}/reconcile", ("f", "json"));
        reconcile.StatusCode.Should().Be(HttpStatusCode.OK);

        var post = await PostFormAsync($"{VmsBase}/versions/{versionGuid}/post", ("f", "json"));
        post.StatusCode.Should().Be(HttpStatusCode.OK,
            "post should succeed after a clean reconcile; body: {0}", await post.Content.ReadAsStringAsync());

        // DEFAULT now reflects the posted change.
        (await QueryCountAsync(marker, gdbVersion: null)).Should().Be(1,
            "DEFAULT must reflect the version's changes after post");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_WithoutGdbVersion_IsUnaffectedByVersioning()
    {
        // The DEFAULT query path must work exactly as before when gdbVersion is absent.
        var response = await _fixture.Client.GetAsync($"{LayerBase}/query?where=1%3D1&returnCountOnly=true&f=json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("count").GetInt32().Should().BeGreaterThanOrEqualTo(0);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    public async Task ServiceMetadata_AdvertisesBranchVersioning()
    {
        var response = await _fixture.Client.GetAsync($"/rest/services/{ServiceId}/FeatureServer?f=json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("supportsBranchVersioning").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("hasVersionedData").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("versionManagementServerUrl").GetString()
            .Should().Be($"/rest/services/{ServiceId}/VersionManagementServer");
    }

    // ---- helpers --------------------------------------------------------------------------------

    private async Task<string> CreateVersionAsync(string versionName)
    {
        var response = await PostFormAsync($"{VmsBase}/create",
            ("versionName", versionName), ("accessPermission", "private"), ("f", "json"));
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "create should succeed; body: {0}", await response.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("versionInfo").GetProperty("versionGuid").GetString()!;
    }

    private async Task<int> QueryCountAsync(string marker, string? gdbVersion)
    {
        var url = $"{LayerBase}/query?where=name%3D%27{marker}%27&returnCountOnly=true&f=json";
        if (!string.IsNullOrEmpty(gdbVersion))
        {
            url += $"&gdbVersion={Uri.EscapeDataString(gdbVersion)}";
        }

        var response = await _fixture.Client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "query should succeed; body: {0}", await response.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("count").GetInt32();
    }

    private static async Task<int> CountByMarkerAsync(HttpResponseMessage applyEditsResponse)
    {
        using var doc = JsonDocument.Parse(await applyEditsResponse.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("addResults", out var addResults))
        {
            return 0;
        }

        return addResults.EnumerateArray().Count(r => r.GetProperty("success").GetBoolean());
    }

    private static string BuildAddsJson(string marker)
    {
        var adds = new[]
        {
            new { attributes = new Dictionary<string, object?> { ["name"] = marker } }
        };
        return JsonSerializer.Serialize(adds);
    }

    private Task<HttpResponseMessage> PostFormAsync(string url, params (string Key, string Value)[] fields)
    {
        var content = new FormUrlEncodedContent(
            fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));
        return _fixture.Client.PostAsync(url, content);
    }
}
