// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;
using Xunit.Abstractions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.VersionManagementServer;

/// <summary>Branch edit validation must read the target version, including earlier partial edits.</summary>
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerBranchEditSnapshotTests(BranchEditSnapshotFixture fixture, ITestOutputHelper output)
    : IClassFixture<BranchEditSnapshotFixture>
{
    private const string Service = "/rest/services/" + BranchVersioningPublicationFixture.ServiceName + "/FeatureServer";
    private const string Layer = Service + "/0";
    private IVersionManager Manager => fixture.App.GetService<IVersionManager>();

    [IntegrationTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/applyEdits")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task PartialUpdates_ReadBranchSnapshot_AndPreserveDefault(bool seedDefault, bool serviceLevel)
    {
        var marker = "snapshot_" + Guid.NewGuid().ToString("N");
        long? baseId = null;
        Guid? versionId = null;
        try
        {
            if (seedDefault)
            {
                baseId = await AddAsync(marker, null);
            }
            versionId = (await Manager.CreateAsync(new CreateVersionRequest(marker, "alice", VersionAccess.Private))).VersionId;
            var objectId = baseId ?? await AddAsync(marker, versionId);
            await AssertDefaultAsync(objectId, seedDefault, marker);

            var renamed = marker + "_branch";
            var nameUpdate = JsonSerializer.Serialize(new[] { new { attributes = new { objectid = objectId, name = renamed } } });
            AssertEdit(await EditAsync("updates", nameUpdate, versionId, serviceLevel), "updateResults", serviceLevel);
            var first = await QueryAsync(objectId, versionId);
            AssertRow(first, renamed, 10, 20);

            // This geometry-only update must merge the branch's renamed attributes, not DEFAULT's old name.
            var geometryUpdate = JsonSerializer.Serialize(new[]
            {
                new { attributes = new { objectid = objectId }, geometry = new { x = 11, y = 21, spatialReference = new { wkid = 4326 } } }
            });
            AssertEdit(await EditAsync("updates", geometryUpdate, versionId, serviceLevel), "updateResults", serviceLevel);
            var second = await QueryAsync(objectId, versionId);
            AssertRow(second, renamed, 11, 21);
            await AssertDefaultAsync(objectId, seedDefault, marker);
        }
        finally
        {
            if (versionId is { } version)
            {
                (await Manager.DeleteAsync(version)).Should().BeTrue();
            }
            if (baseId is { } objectId)
            {
                AssertEdit(await EditAsync("deletes", objectId.ToString(CultureInfo.InvariantCulture), null, false), "deleteResults", false);
            }
        }
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/applyEdits")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Delete_BranchCreatedFeature_UsesBranchVisibility(bool serviceLevel)
    {
        var marker = "snapshot_delete_" + Guid.NewGuid().ToString("N");
        var version = await Manager.CreateAsync(new CreateVersionRequest(marker, "alice", VersionAccess.Private));
        try
        {
            var objectId = await AddAsync(marker, version.VersionId);
            AssertRow(await QueryAsync(objectId, version.VersionId), marker, 10, 20);
            await AssertDefaultAsync(objectId, false, marker);
            AssertEdit(await EditAsync("deletes", objectId.ToString(CultureInfo.InvariantCulture), version.VersionId, serviceLevel),
                "deleteResults", serviceLevel);
            (await QueryAsync(objectId, version.VersionId)).GetProperty("features").GetArrayLength().Should().Be(0);
            await AssertDefaultAsync(objectId, false, marker);
        }
        finally
        {
            (await Manager.DeleteAsync(version.VersionId)).Should().BeTrue();
        }
    }

    private async Task<long> AddAsync(string marker, Guid? version)
    {
        var adds = JsonSerializer.Serialize(new[]
        {
            new { attributes = new { name = marker }, geometry = new { x = 10, y = 20, spatialReference = new { wkid = 4326 } } }
        });
        var body = await EditAsync("adds", adds, version, false);
        AssertEdit(body, "addResults", false);
        return body.GetProperty("addResults")[0].GetProperty("objectId").GetInt64();
    }

    private Task<JsonElement> EditAsync(string operation, string payload, Guid? version, bool serviceLevel)
    {
        var values = new Dictionary<string, string> { ["f"] = "json", ["rollbackOnFailure"] = "true" };
        if (version is { } id)
        {
            values["gdbVersion"] = id.ToString();
        }
        if (serviceLevel)
        {
            values["edits"] = "[{\"id\":0,\"" + operation + "\":" + (operation == "deletes" ? "[" + payload + "]" : payload) + "}]";
        }
        else
        {
            values[operation] = payload;
        }
        return SendAsync((serviceLevel ? Service : Layer) + "/applyEdits", values, true);
    }

    private Task<JsonElement> QueryAsync(long objectId, Guid? version)
    {
        var values = new Dictionary<string, string>
        {
            ["f"] = "json",
            ["objectIds"] = objectId.ToString(CultureInfo.InvariantCulture),
            ["outFields"] = "objectid,name",
            ["returnGeometry"] = "true"
        };
        if (version is { } id)
        {
            values["gdbVersion"] = id.ToString();
        }
        return SendAsync(Layer + "/query", values, false);
    }

    private async Task AssertDefaultAsync(long objectId, bool expected, string marker)
    {
        var body = await QueryAsync(objectId, null);
        if (expected)
        {
            AssertRow(body, marker, 10, 20);
        }
        else
        {
            body.GetProperty("features").GetArrayLength().Should().Be(0, "branch-only features must remain absent from DEFAULT");
        }
    }

    private static void AssertRow(JsonElement body, string name, double x, double y)
    {
        body.GetProperty("features").GetArrayLength().Should().Be(1, body.ToString());
        var row = body.GetProperty("features")[0];
        row.GetProperty("attributes").GetProperty("name").GetString().Should().Be(name);
        row.GetProperty("geometry").GetProperty("x").GetDouble().Should().Be(x);
        row.GetProperty("geometry").GetProperty("y").GetDouble().Should().Be(y);
    }

    private static void AssertEdit(JsonElement body, string resultName, bool serviceLevel)
    {
        body.TryGetProperty("error", out _).Should().BeFalse(body.ToString());
        var result = serviceLevel ? body.GetProperty("editResults")[0] : body;
        result.GetProperty(resultName)[0].GetProperty("success").GetBoolean().Should().BeTrue(body.ToString());
    }

    private async Task<JsonElement> SendAsync(string path, Dictionary<string, string> values, bool post)
    {
        using var client = fixture.App.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token);
        client.DefaultRequestHeaders.Referrer = new Uri(BranchEditSnapshotFixture.Referer);
        using var content = new FormUrlEncodedContent(values);
        using var response = post ? await client.PostAsync(path, content)
            : await client.GetAsync(path + "?" + await content.ReadAsStringAsync());
        var body = await response.Content.ReadAsStringAsync();
        output.WriteLine("{0} HTTP {1}: {2}", path, (int)response.StatusCode, body);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}

/// <summary>Owned managed publication and a genuine editor principal for branch snapshot regressions.</summary>
public sealed class BranchEditSnapshotFixture : IAsyncLifetime
{
    internal const string Referer = "https://branch-snapshot-proof.example/";
    internal WebAppFixture App { get; } = new();
    internal string Token { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        App.WithTestLicense(HonuaEdition.Enterprise);
        App.ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            builder.UseSetting("Capabilities:Experimental:versioning.branch:Enabled", "true");
        });
        await App.InitializeAsync();
        BranchVersioningPublicationFixture.ConfigureManagedPublications(App);
        App.EnableV2ServiceEditingCapabilities(BranchVersioningPublicationFixture.ServiceName, ["Create", "Update", "Delete"]);
        Token = (await App.GetService<IPortalTokenIssuer>().IssueAsync(new PortalTokenIssueRequest(
            "alice", "alice", TenantId: null, Roles: ["data-editor:" + BranchVersioningPublicationFixture.ServiceName],
            PortalTokenClientType.Referer, Referer, DateTimeOffset.UtcNow.AddMinutes(30)), CancellationToken.None)).Token;
    }

    public Task DisposeAsync() => App.DisposeAsync();
}
